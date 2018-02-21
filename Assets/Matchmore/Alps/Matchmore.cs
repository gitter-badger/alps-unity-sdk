using UnityEngine;
using Alps.Client;
using Alps.Model;
using Alps.Api;
using System;
using System.Collections.Generic;
using MatchmorePersistence;
using System.Collections;
using System.Collections.ObjectModel;

public partial class Matchmore
{
    private static Matchmore _instance;
    public static readonly string API_VERSION = "v5";
    public static readonly string PRODUCTION = "api.matchmore.io";
    private ApiClient _client;
    private DeviceApi _deviceApi;
    private StateManager _state;
    private GameObject _obj;
    private CoroutineWrapper _coroutine;
    private string _environment;
    private string _apiKey;
    private bool _secured;
    private int? _servicePort;
    private Dictionary<string, IMatchMonitor> _monitors = new Dictionary<string, IMatchMonitor>();
    private List<EventHandler<MatchReceivedEventArgs>> _eventHandlers = new List<EventHandler<MatchReceivedEventArgs>>();
    private readonly Config _config;

    public event EventHandler<MatchReceivedEventArgs> MatchReceived
    {
        add
        {
            foreach (var monitor in _monitors)
            {
                _eventHandlers.Add(value);
                monitor.Value.MatchReceived += value;
            }
        }
        remove
        {
            foreach (var monitor in _monitors)
            {
                _eventHandlers.Remove(value);
                monitor.Value.MatchReceived -= value;
            }
        }
    }

    [Flags]
    public enum MatchChannel
    {
        Polling = 0,
        Websocket = 1
    }

    public Dictionary<string, IMatchMonitor> Monitors
    {
        get
        {
            return _monitors;
        }
    }

    public Device MainDevice
    {
        get
        {
            return _state.Device;
        }
        private set
        {
            _state.Device = value;
            _state.Save();
        }
    }

    public string ApiUrl
    {
        get
        {
            if (_environment != null)
            {
                var protocol = _secured ? "https" : "http";
                var port = _servicePort == null ? "" : ":" + _servicePort;
                return String.Format("{2}://{0}{3}/{1}", _environment, API_VERSION, protocol, port);
            }
            else
            {
                var protocol = _secured ? "https" : "http";
                return String.Format("{0}://{1}/{2}", protocol, PRODUCTION, API_VERSION);
            }

        }
    }

    public static void Configure(string apiKey){
        Configure(Config.WithApiKey(apiKey));
    }

    public static void Configure(Config config)
    {
        if (Instance != null)
        {
            throw new InvalidOperationException("Matchmore static instance already configured");
        }
        Instance = new Matchmore(config);
    }

    public static void Reset()
    {
        if (_instance != null)
        {
            _instance.CleanUp();
            _instance = null;
        }
    }

    public static Matchmore Instance
    {
        get
        {
            return _instance;
        }
        private set
        {
            _instance = value;
        }
    }

    public Matchmore(Config config)
    {
        _config = config;

        MatchmoreLogger.Enabled = config.LoggingEnabled;

        if (string.IsNullOrEmpty(config.ApiKey))
        {
            throw new ArgumentException("Api key null or empty");
        }

        _apiKey = config.ApiKey;
        _servicePort = config.ServicePort;
        _environment = config.Environment ?? PRODUCTION;
        _secured = config.UseSecuredCommunication;
        InitGameObjects();

        _state = new StateManager(_environment, config.PersistenceFile);

        if (MainDevice == null)
        {
            CreateDevice(new MobileDevice(), makeMain: true);
        }

        _coroutine.SetupContinuousRoutine("persistence", _state.PruneDead);
        StartLocationService();
    }

    public void StartLocationService()
    {
        _coroutine.RunOnce("location_service", StartLocationServiceCoroutine());
    }

    public void WipeData()
    {
        _state.WipeData();
    }

    IEnumerator StartLocationServiceCoroutine()
    {
        // First, check if user has location service enabled
        if (!Input.location.isEnabledByUser)
        {
            MatchmoreLogger.Debug("Location service disabled by user");
# if !UNITY_IOS
            //https://docs.unity3d.com/ScriptReference/LocationService-isEnabledByUser.html
            //if it is IOS we do not break here
            yield break;
#endif
        }


        // Start service before querying location
        Input.location.Start();

        // Wait until service initializes
        int maxWait = 20;
        while (Input.location.status == LocationServiceStatus.Initializing && maxWait > 0)
        {
            yield return new WaitForSeconds(1);
            maxWait--;
        }

        // Service didn't initialize in 20 seconds
        if (maxWait < 1)
        {
            yield break;
        }

        // Connection has failed
        if (Input.location.status == LocationServiceStatus.Failed)
        {
            //print("Unable to determine device location");
            yield break;
        }

        _coroutine.SetupContinuousRoutine("location_service", UpdateLocation);
    }

    private void UpdateLocation()
    {
        if (Input.location.status == LocationServiceStatus.Running)
        {
            var location = Input.location.lastData;

            UpdateLocation(new Location
            {
                Latitude = location.latitude,
                Longitude = location.longitude,
                Altitude = location.altitude
            });
        }
    }

    private void InitGameObjects()
    {
        _client = new ApiClient(ApiUrl);
        _client.AddDefaultHeader("api-key", _apiKey);
        _deviceApi = new DeviceApi(_client);
        _obj = new GameObject("MatchMoreObject");
        if(_config.LoggingEnabled){
            MatchmoreLogger.Context = _obj;
        }
        _coroutine = _obj.AddComponent<CoroutineWrapper>();
    }

    public Device CreateDevice(Device device, bool makeMain = false)
    {
        if (_state == null)
        {
            throw new InvalidOperationException("Persistence wasn't setup!!!");
        }

        Device createdDevice = null;

        if (!string.IsNullOrEmpty(device.Id))
        {
            UnityEngine.MonoBehaviour.print("Device ID will be ignored!!!");
        }

        if (device is PinDevice)
        {
            var pinDevice = device as PinDevice;
            pinDevice.DeviceType = Alps.Model.DeviceType.Pin;
            if (pinDevice.Location == null)
            {
                throw new ArgumentException("Location required for Pin Device");
            }

            createdDevice = pinDevice;
        }

        if (device is MobileDevice)
        {
            var mobileDevice = device as MobileDevice;
            mobileDevice.DeviceType = Alps.Model.DeviceType.Mobile;

            mobileDevice.Name = string.IsNullOrEmpty(mobileDevice.Name) ? SystemInfo.deviceModel : mobileDevice.Name;
            mobileDevice.Platform = string.IsNullOrEmpty(mobileDevice.Platform) ? Application.platform.ToString() : mobileDevice.Platform;
            mobileDevice.DeviceToken = string.IsNullOrEmpty(mobileDevice.DeviceToken) ? "" : mobileDevice.DeviceToken;

            createdDevice = mobileDevice;
        }

        if (device is IBeaconDevice)
        {
            var ibeaconDevice = device as IBeaconDevice;
            ibeaconDevice.DeviceType = Alps.Model.DeviceType.IBeacon;
            if (ibeaconDevice.Major == null)
            {
                throw new ArgumentException("Major required for Ibeacon Device");
            }

            if (ibeaconDevice.Minor == null)
            {
                throw new ArgumentException("Minor required for Ibeacon Device");
            }

            if (string.IsNullOrEmpty(ibeaconDevice.Name))
            {
                throw new ArgumentException("Name required for Ibeacon Device");
            }

            createdDevice = ibeaconDevice;
        }

        var deviceInBackend = _deviceApi.CreateDevice(createdDevice);
        //only mobile can be considered as a main device
        if (makeMain && createdDevice is MobileDevice)
        {
            MainDevice = deviceInBackend;
        }
        return deviceInBackend;
    }

    public PinDevice CreatePinDevice(PinDevice pinDevice)
    {
        pinDevice.DeviceType = Alps.Model.DeviceType.Pin;
        if (pinDevice.Location == null)
        {
            throw new ArgumentException("Location required for Pin Device");
        }

        var createdDevice = _deviceApi.CreateDevice(pinDevice);

        //The generated swagger api returns a generic device partially losing the information about the pin.
        //We rewrite the data to fit the pin device contract.
        var createdPin = new PinDevice
        {
            Id = createdDevice.Id,
            CreatedAt = createdDevice.CreatedAt,
            DeviceType = createdDevice.DeviceType,
            Location = pinDevice.Location,
            Group = createdDevice.Group,
            Name = createdDevice.Name,
            UpdatedAt = createdDevice.UpdatedAt
        };
        _state.AddPinDevice(createdPin);

        return createdPin;
    }

    public Tuple<PinDevice, IMatchMonitor> CreatePinDeviceAndStartListening(PinDevice pinDevice, MatchChannel channel)
    {
        var createdDevice = CreatePinDevice(pinDevice);
        var monitor = SubscribeMatches(channel, createdDevice);

        return Tuple.New(createdDevice, monitor);
    }

    public Subscription CreateSubscription(Subscription sub, Device device = null)
    {
        var usedDevice = device != null ? device : _state.Device;
        return CreateSubscription(sub, usedDevice.Id);
    }

    public Subscription CreateSubscription(Subscription sub, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            throw new ArgumentException("Device Id null or empty");
        }

        var _sub = _deviceApi.CreateSubscription(deviceId, sub);
        _state.AddSubscription(_sub);
        return _sub;
    }

    public Publication CreatePublication(Publication pub, Device device = null)
    {
        var usedDevice = device != null ? device : _state.Device;
        return CreatePublication(pub, usedDevice.Id);
    }

    public Publication CreatePublication(Publication pub, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            throw new ArgumentException("Device Id null or empty");
        }

        var _pub = _deviceApi.CreatePublication(deviceId, pub);
        _state.AddPublication(_pub);
        return _pub;
    }

    public Location UpdateLocation(Location location, Device device = null)
    {
        var usedDevice = device != null ? device : _state.Device;
        return UpdateLocation(location, usedDevice.Id);
    }

    public Location UpdateLocation(Location location, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            throw new ArgumentException("Device Id null or empty");
        }

        if (location.Altitude == null)
            location.Altitude = 0;

        return _deviceApi.CreateLocation(deviceId, location);
    }

    public List<Match> GetMatches(Device device = null)
    {
        var usedDevice = device != null ? device : _state.Device;
        return GetMatches(usedDevice.Id);
    }

    public List<Match> GetMatches(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            throw new ArgumentException("Device Id null or empty");
        }

        return _deviceApi.GetMatches(deviceId);
    }

    public IMatchMonitor SubscribeMatches(MatchChannel channel, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            throw new ArgumentException("Device Id null or empty");
        }

        return SubscribeMatches(channel, FindDevice(deviceId));
    }

    public IMatchMonitor SubscribeMatches(MatchChannel channel, Device device = null)
    {
        var deviceToSubscribe = device == null ? _state.Device : device;
        IMatchMonitor monitor = null;
        switch (channel)
        {
            case MatchChannel.Polling:
                monitor = CreatePollingMonitor(deviceToSubscribe);
                break;
            case MatchChannel.Websocket:
                monitor = CreateWebsocketMonitor(deviceToSubscribe);
                break;
            default:
                break;
        }

        if (monitor == null)
        {
            throw new ArgumentException(String.Format("{0} is an unrecognized channel", channel));
        }

        if (_monitors.ContainsKey(deviceToSubscribe.Id))
        {
            _monitors[deviceToSubscribe.Id].Stop();
            _monitors.Remove(deviceToSubscribe.Id);
        }

        foreach (var handler in _eventHandlers)
        {
            monitor.MatchReceived += handler;
        }

        _monitors.Add(deviceToSubscribe.Id, monitor);

        return monitor;
    }

    private Device FindDevice(string deviceId)
    {
        if (_state.Device.Id == deviceId)
            return _state.Device;
        else
            return _state.Pins.Find(pin => pin.Id == deviceId);
    }

    public IEnumerable<Subscription> ActiveSubscriptions
    {
        get
        {
            return _state.ActiveSubscriptions.AsReadOnly();
        }
    }

    public IEnumerable<Publication> ActivePublications
    {
        get
        {
            return _state.ActivePublications.AsReadOnly();
        }
    }

    public void CleanUp()
    {
        foreach (var monitor in _monitors)
        {
            monitor.Value.Stop();
        }

        _monitors.Clear();
        _monitors = null;

        if (_coroutine != null)
        {
            if (Application.isEditor)
                UnityEngine.Object.DestroyImmediate(_coroutine);
            else
                UnityEngine.Object.Destroy(_coroutine);
        }
        if (_obj != null)
        {
            if (Application.isEditor)
                UnityEngine.Object.DestroyImmediate(_obj);
            else
                UnityEngine.Object.Destroy(_obj);
        }
    }

    private PollingMatchMonitor CreatePollingMonitor(Device device)
    {
        return new PollingMatchMonitor(device, _deviceApi, _coroutine, id => _monitors.Remove(id));
    }

    private WebsocketMatchMonitor CreateWebsocketMonitor(Device device)
    {
        return new WebsocketMatchMonitor(device, _config, _deviceApi, _coroutine, id => _monitors.Remove(id));
    }
}
