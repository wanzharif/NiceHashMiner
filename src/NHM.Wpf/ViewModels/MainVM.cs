﻿using NHM.Common;
using NHM.Wpf.Annotations;
using NiceHashMiner;
using NiceHashMiner.Configs;
using NiceHashMiner.Devices;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Input;
using NHM.Common.Enums;
using NHM.Wpf.ViewModels.Models;
using NiceHashMiner.Mining;
using NiceHashMiner.Stats;
using NiceHashMiner.Switching;
using NiceHashMiner.Utils;

namespace NHM.Wpf.ViewModels
{
    public class MainVM : BaseVM
    {
        #region Fake placeholder

        public class DeviceInfo : INotifyPropertyChanged
        {
            public bool Enabled { get; set; }
            public string Device { get; }
            public string Status => Enabled ? "Stopped" : "Disabled";
            private int? _temp;
            public int? Temp
            {
                get => _temp;
                set
                {
                    _temp = value;
                    SetPropertyChanged();
                }
            }
            public int Load { get; }
            public int? RPM { get; }
            public string AlgoDetail { get; }
            public string ButtonText => Enabled ? "Start" : "N/A";

            public DeviceInfo(bool enabled, string dev, int? temp, int load, int? rpm, string detail)
            {
                Enabled = enabled;
                Device = dev;
                Temp = temp;
                Load = load;
                RPM = rpm;
                AlgoDetail = detail;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            [NotifyPropertyChangedInvocator]
            protected virtual void SetPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        #endregion

        private readonly Timer _updateTimer;

        public class DeviceData : NotifyChangedBase
        {
            public ComputeDevice Dev { get; }

            public bool Enabled
            {
                get => Dev.Enabled;
                set
                {
                    ApplicationStateManager.SetDeviceEnabledState(this, (Dev.B64Uuid, value));
                    OnPropertyChanged();
                }
            }

            public string AlgoOptions
            {
                get
                {
                    var enabledAlgos = Dev.AlgorithmSettings.Count(a => a.Enabled);
                    var benchedAlgos = Dev.AlgorithmSettings.Count(a => !a.BenchmarkNeeded);
                    return $"{Dev.AlgorithmSettings.Count} / {enabledAlgos} / {benchedAlgos}";
                }
            }

            public string ButtonLabel
            {
                get
                {
                    // assume disabled
                    var buttonLabel = "N/A";
                    if (Dev.State == DeviceState.Stopped)
                    {
                        buttonLabel = "Start";
                    }
                    else if (Dev.State == DeviceState.Mining || Dev.State == DeviceState.Benchmarking)
                    {
                        buttonLabel = "Stop";
                    }
                    return Translations.Tr(buttonLabel);
                }
            }

            public ICommand StartStopCommand { get; }

            public DeviceData(ComputeDevice dev)
            {
                Dev = dev;

                StartStopCommand = new BaseCommand(StartStopClick);

                Dev.PropertyChanged += DevOnPropertyChanged;
            }

            private void DevOnPropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(Dev.State))
                {
                    OnPropertyChanged(nameof(ButtonLabel));
                }
                else if (e.PropertyName == nameof(Dev.Enabled))
                {
                    OnPropertyChanged(nameof(Enabled));
                }
            }

            public void RefreshDiag()
            {
                Dev.OnPropertyChanged(nameof(Dev.Load));
                Dev.OnPropertyChanged(nameof(Dev.Temp));
                Dev.OnPropertyChanged(nameof(Dev.FanSpeed));
            }

            private void StartStopClick(object param)
            {
                // TODO
            }

            public static implicit operator DeviceData(ComputeDevice dev)
            {
                return new DeviceData(dev);
            }
        }

        public class MiningData : NotifyChangedBase
        {
            public ComputeDevice Dev { get; }

            private MiningStats.DeviceMiningStats _stats;
            public MiningStats.DeviceMiningStats Stats
            {
                get => _stats;
                set
                {
                    _stats = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Hashrate));
                    OnPropertyChanged(nameof(Payrate));
                    OnPropertyChanged(nameof(StateName));
                    OnPropertyChanged(nameof(Speeds));
                    OnPropertyChanged(nameof(FiatPayrate));
                    OnPropertyChanged(nameof(PowerUsage));
                    OnPropertyChanged(nameof(PowerCost));
                    OnPropertyChanged(nameof(Profit));
                }
            }

            public double Hashrate => Stats?.Speeds?.Count > 0 ? Stats.Speeds[0].speed : 0;

            public IEnumerable<Hashrate> Speeds => Stats?.Speeds?.Select(s => (Hashrate) s);

            public double Payrate => (Stats?.TotalPayingRate() ?? 0) * 1000;

            public double FiatPayrate => ExchangeRateApi.ConvertFromBtc(Payrate / 1000);

            public double PowerUsage => Stats?.GetPowerUsage() ?? 0;

            public double PowerCost
            {
                get
                {
                    var cost = Stats?.PowerCost(ExchangeRateApi.GetKwhPriceInBtc()) ?? 0;
                    return ExchangeRateApi.ConvertFromBtc(cost);
                }
            }

            public double Profit
            {
                get
                {
                    var cost = Stats?.TotalPayingRateDeductPowerCost(ExchangeRateApi.GetKwhPriceInBtc()) ?? 0;
                    return ExchangeRateApi.ConvertFromBtc(cost);
                }
            }

            public string StateName
            {
                get
                {
                    if (Stats == null) return Dev.State.ToString();

                    var firstAlgo = Stats.Speeds.Count > 0 ? Stats.Speeds[0].type : AlgorithmType.NONE;
                    var secAlgo = Stats.Speeds.Count > 1 ? Stats.Speeds[1].type : AlgorithmType.NONE;
                    var algoName = Helpers.GetNameFromAlgorithmTypes(firstAlgo, secAlgo);

                    return $"{algoName} ({Stats.MinerName})";
                }
            }

            public MiningData(ComputeDevice dev)
            {
                Dev = dev;

                Dev.PropertyChanged += DevOnPropertyChanged;
            }

            private void DevOnPropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(Dev.State))
                    OnPropertyChanged(nameof(StateName));
            }
        }

        private IEnumerable<DeviceData> _devices;
        public IEnumerable<DeviceData> Devices
        {
            get => _devices;
            set
            {
                _devices = value;
                OnPropertyChanged();
            }
        }

        private IEnumerable<MiningData> _miningDevs;
        public IEnumerable<MiningData> MiningDevs
        {
            get => _miningDevs;
            set
            {
                _miningDevs = value;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<string> ServiceLocations => StratumService.MiningLocationNames;

        public int ServiceLocationIndex
        {
            get => ConfigManager.GeneralConfig.ServiceLocation;
            set => ConfigManager.GeneralConfig.ServiceLocation = value;
        }

        public string BtcAddress
        {
            get => ConfigManager.GeneralConfig.BitcoinAddress;
            set => ConfigManager.GeneralConfig.BitcoinAddress = value;
        }

        public string WorkerName
        {
            get => ConfigManager.GeneralConfig.WorkerName;
            set => ConfigManager.GeneralConfig.WorkerName = value;
        }

        public MiningState State => MiningState.Instance;

        public string CurrencyPerTime => $"{ExchangeRateApi.ActiveDisplayCurrency}/{TimeFactor.UnitType}";

        public string ProfitPerTime => $"Profit ({CurrencyPerTime})";

        public MainVM()
        {
            //Devices = new ObservableCollection<DeviceInfo>
            //{
            //    new DeviceInfo(false, "CPU#1 Intel(R) Core(TM) i7-8700k CPU @ 3.70GHz", null, 10, null, "3 / 3 / 0"),
            //    new DeviceInfo(true, "GPU#1 EVGA GeForce GTX 1080 Ti", 64, 0, 1550, "36 / 27 / 5"),
            //    new DeviceInfo(true, "GPU#2 EVGA GeForce GTX 1080 Ti", 54, 0, 1150, "36 / 27 / 3"),

            //};

            _updateTimer = new Timer(1000);
            _updateTimer.Elapsed += UpdateTimerOnElapsed;

            ExchangeRateApi.CurrencyChanged += (_, s) =>
            {
                OnPropertyChanged(nameof(CurrencyPerTime));
                OnPropertyChanged(nameof(ProfitPerTime));
            };
        }

        // TODO I don't like this way, a global refresh and notify would be better
        private void UpdateTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (Devices == null) return;
            foreach (var dev in Devices)
            {
                dev.RefreshDiag();
            }
        }

        public async Task InitializeNhm(IStartupLoader sl)
        {
            await ApplicationStateManager.InitializeManagersAndMiners(sl);

            Devices = new ObservableCollection<DeviceData>(AvailableDevices.Devices.Select(d => (DeviceData) d));
            MiningDevs = new ObservableCollection<MiningData>(AvailableDevices.Devices.Select(d => new MiningData(d)));

            MiningStats.DevicesMiningStats.CollectionChanged += DevicesMiningStatsOnCollectionChanged;

            _updateTimer.Start();

            // TODO auto-start mining
        }

        private void DevicesMiningStatsOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                case NotifyCollectionChangedAction.Replace:
                    foreach (var stat in e.NewItems.OfType<MiningStats.DeviceMiningStats>())
                    {
                        var miningDev = MiningDevs.FirstOrDefault(d => d.Dev.Uuid == stat.DeviceUUID);
                        if (miningDev != null) miningDev.Stats = stat;
                    }

                    break;
                case NotifyCollectionChangedAction.Reset:
                    foreach (var miningDev in MiningDevs)
                    {
                        miningDev.Stats = null;
                    }

                    break;
            }
        }

        public async Task StartMining()
        {
            var hasData = NHSmaData.HasData;

            // TODO there is a better way..
            for (var i = 0; i < 10; i++)
            {
                if (hasData) break;
                await Task.Delay(1000);
                hasData = NHSmaData.HasData;
                Logger.Info("NICEHASH", $"After {i}s has data: {hasData}");
            }

            if (!hasData) return;

            // TODO there is a mess of blocking and not-awaited async code down the line, 
            // Just wrapping with Task.Run here for now

            await Task.Run(() => { ApplicationStateManager.StartAllAvailableDevices(); });
        }

        public async Task StopMining()
        {
            await Task.Run(() => { ApplicationStateManager.StopAllDevice(); });
        }
    }
}