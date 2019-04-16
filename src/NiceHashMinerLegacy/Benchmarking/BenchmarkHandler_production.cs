﻿// PRODUCTION
#if !(TESTNET || TESTNETDEV)
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NiceHashMiner.Algorithms;
using NiceHashMiner.Benchmarking.BenchHelpers;
using NiceHashMiner.Configs;
using NiceHashMiner.Devices;
using NiceHashMiner.Miners;
using NiceHashMiner.Miners.Grouping;
using NiceHashMinerLegacy.Common;
using NiceHashMinerLegacy.Common.Enums;
using NiceHashMiner.Plugin;
using NiceHashMiner.Interfaces;

namespace NiceHashMiner.Benchmarking
{
    public class BenchmarkHandler
    {
        CancellationTokenSource _stopBenchmark;

        // OLD
        private readonly Queue<Algorithm> _benchmarkAlgorithmQueue;
        private readonly int _benchmarkAlgorithmsCount;
        private readonly List<string> _benchmarkFailedAlgo = new List<string>();
        private readonly IBenchmarkForm _benchmarkForm;
        private readonly BenchmarkPerformanceType _performanceType;

        private PowerHelper _powerHelper;

        public BenchmarkHandler(ComputeDevice device, Queue<Algorithm> algorithms, IBenchmarkForm form,
            BenchmarkPerformanceType performance)
        {
            _stopBenchmark = new CancellationTokenSource();
            Device = device;
            _benchmarkAlgorithmQueue = algorithms;
            _benchmarkForm = form;
            _performanceType = performance;

            _benchmarkAlgorithmsCount = _benchmarkAlgorithmQueue.Count;
            _powerHelper = new PowerHelper(device);
        }

        public ComputeDevice Device { get; }

        public void Start()
        {
            // TODO ditch the thread and use Task
            var thread = new Thread(async () => await Benchmark());
            if (thread.Name == null)
                thread.Name = $"dev_{Device.DeviceType}-{Device.ID}_benchmark";
            thread.Start();
        }

        private async Task Benchmark()
        {
            Algorithm currentAlgorithm = null;
            while (_benchmarkAlgorithmQueue.Count > 0)
            {
                try
                {
                    if (_stopBenchmark.IsCancellationRequested) break;
                    currentAlgorithm = _benchmarkAlgorithmQueue.Dequeue();
                    _benchmarkForm.AddToStatusCheck(Device, currentAlgorithm);
                    await BenchmarkAlgorithm(currentAlgorithm);
                    if (_stopBenchmark.IsCancellationRequested) break;
                    _benchmarkForm.StepUpBenchmarkStepProgress();
                    ConfigManager.CommitBenchmarksForDevice(Device);
                }
                catch (Exception e)
                {
                    Helpers.ConsolePrint($"BenchmarkHandler-{Device.GetFullName()}", $"Exception {e}");
                }
            }
            currentAlgorithm?.ClearBenchmarkPending();
            // don't show unbenchmarked algos if user canceled
            if (_stopBenchmark.IsCancellationRequested) return;
            _benchmarkForm.EndBenchmarkForDevice(Device, _benchmarkFailedAlgo.Count > 0);
        }

        private async Task BenchmarkAlgorithm(Algorithm algo)
        {
            var currentMiner = MinerFactory.CreateMiner(Device, algo);
            if (currentMiner == null) return;

            _benchmarkForm.AddToStatusCheck(Device, algo);
            if (algo is DualAlgorithm dualAlgo && dualAlgo.TuningEnabled && dualAlgo.StartTuning())
            {
                await BenchmarkAlgorithmDual(currentMiner, dualAlgo);
            }
            else if (algo is PluginAlgorithm pAlgo)
            {
                await BenchmarkPluginAlgorithm(pAlgo);
            }
            else
            {
                await BenchmarkAlgorithmOnce(currentMiner, algo);
            }
        }

        private async Task BenchmarkAlgorithmOnce(Miner currentMiner, Algorithm algo)
        {
            currentMiner.InitBenchmarkSetup(new MiningPair(Device, algo));
            var time = BenchmarkTimes.GetTime(_performanceType, Device.DeviceType);

            var benchTaskResult = currentMiner.BenchmarkStartAsync(time, _stopBenchmark.Token);
            _powerHelper.Start();
            var result = await benchTaskResult;
            var power = _powerHelper.Stop();
            algo.PowerUsage = power;

            _benchmarkForm.RemoveFromStatusCheck(Device, algo);
            if (!result.Success)
            {
                // add new failed list
                _benchmarkFailedAlgo.Add(algo.AlgorithmName);
                _benchmarkForm.SetCurrentStatus(Device, algo, result.Status);
            }
            else
            {
                algo.ClearBenchmarkPending();
                _benchmarkForm.SetCurrentStatus(Device, algo, "");
            }
        }

        private async Task BenchmarkAlgorithmDual(Miner currentMiner, DualAlgorithm dualAlgo)
        {
            var anyResultSuccess = false;
            var lastStatus = "";
            do
            {
                if (_stopBenchmark.IsCancellationRequested) break;

                currentMiner.InitBenchmarkSetup(new MiningPair(Device, dualAlgo));
                var time = BenchmarkTimes.GetTime(_performanceType, Device.DeviceType);

                var benchTaskResult = currentMiner.BenchmarkStartAsync(time, _stopBenchmark.Token);
                _powerHelper.Start();
                var result = await benchTaskResult;
                var power = _powerHelper.Stop();
                dualAlgo.SetPowerForCurrent(power);
                anyResultSuccess |= result.Success;
                lastStatus = result.Status;
            }
            while (dualAlgo.IncrementToNextEmptyIntensity());

            _benchmarkForm.RemoveFromStatusCheck(Device, dualAlgo);
            if (!anyResultSuccess)
            {
                // add new failed list
                _benchmarkFailedAlgo.Add(dualAlgo.AlgorithmName);
                _benchmarkForm.SetCurrentStatus(Device, dualAlgo, lastStatus);
            }
            else
            {
                dualAlgo.ClearBenchmarkPending();
                _benchmarkForm.SetCurrentStatus(Device, dualAlgo, "");
            }
        }

        private async Task BenchmarkPluginAlgorithm(PluginAlgorithm algo)
        {
            var plugin = MinerPluginsManager.GetPluginWithUuid(algo.BaseAlgo.MinerID);
            var miner = plugin.CreateMiner();
            var miningPair = new MinerPlugin.MiningPair
            {
                Device = Device.PluginDevice,
                Algorithm = algo.BaseAlgo
            };
            miner.InitMiningPairs(new List<MinerPlugin.MiningPair> { miningPair });
            // fill service since the benchmark might be online. DemoUser.BTC must be used
            miner.InitMiningLocationAndUsername(Globals.MiningLocation[ConfigManager.GeneralConfig.ServiceLocation], DemoUser.BTC);
            _powerHelper.Start();
            var result = await miner.StartBenchmark(_stopBenchmark.Token, _performanceType);
            var power = _powerHelper.Stop();
            if (result.Success || result.AlgorithmTypeSpeeds?.Count > 0)
            {
                algo.BenchmarkSpeed = result.AlgorithmTypeSpeeds.First().Speed;
                algo.PowerUsage = power;
                if (result.AlgorithmTypeSpeeds.Count > 1)
                {
                    Helpers.ConsolePrint("BenchmarkHandler2", $"Has Second speed {result.AlgorithmTypeSpeeds[1].Speed}");

                }
                // set status to empty string it will return speed
                algo.ClearBenchmarkPending();
                _benchmarkForm.SetCurrentStatus(Device, algo, "");
            }
            else
            {
                // add new failed list
                _benchmarkFailedAlgo.Add(algo.AlgorithmName);
                _benchmarkForm.SetCurrentStatus(Device, algo, result.ErrorMessage);
            }
        }

        public void InvokeQuit()
        {
            _stopBenchmark.Cancel();
        }
    }
}
#endif