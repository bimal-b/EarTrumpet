﻿using EarTrumpet.Extensions;
using EarTrumpet.Interop;
using EarTrumpet.Interop.MMDeviceAPI;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace EarTrumpet.DataModel.Internal
{
    class AudioDeviceManager : IMMNotificationClient, IAudioDeviceManager, IAudioDeviceManagerInternal
    {
        public event EventHandler<IAudioDevice> DefaultPlaybackDeviceChanged;
        public event EventHandler<IAudioDeviceSession> SessionCreated;

        public ObservableCollection<IAudioDevice> Devices => _devices;

        private static IPolicyConfig s_PolicyConfigClient = null;

        private IMMDeviceEnumerator _enumerator;
        private IAudioDevice _defaultPlaybackDevice;
        private IAudioDevice _defaultCommunicationsDevice;
        private ObservableCollection<IAudioDevice> _devices = new ObservableCollection<IAudioDevice>();
        private IVirtualDefaultAudioDevice _virtualDefaultDevice;
        private Dispatcher _dispatcher;

        public AudioDeviceManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);

            var devices = _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.ACTIVE);
            uint deviceCount = devices.GetCount();
            for (uint i = 0; i < deviceCount; i++)
            {
                ((IMMNotificationClient)this).OnDeviceAdded(devices.Item(i).GetId());
            }

            // Trigger default logic to register for volume change
            QueryDefaultPlaybackDevice();
            QueryDefaultCommunicationsDevice();

            _virtualDefaultDevice = new VirtualDefaultAudioDevice(this);
        }

        private void QueryDefaultPlaybackDevice()
        {
            IMMDevice device = null;
            try
            {
                device = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070490)
            {
                // Element not found.
            }

            string newDeviceId = device?.GetId();
            var currentDeviceId = _defaultPlaybackDevice?.Id;
            if (currentDeviceId != newDeviceId)
            {
                if (newDeviceId == null)
                {
                    _defaultPlaybackDevice = null;
                }
                else
                {
                    FindDevice(newDeviceId, out _defaultPlaybackDevice);
                }

                DefaultPlaybackDeviceChanged?.Invoke(this, _defaultPlaybackDevice);
            }
        }

        private void QueryDefaultCommunicationsDevice()
        {
            IMMDevice device = null;
            try
            {
                device = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eCommunications);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070490)
            {
                // Element not found.
            }

            string newDeviceId = device?.GetId();
            var currentDeviceId = _defaultCommunicationsDevice?.Id;
            if (currentDeviceId != newDeviceId)
            {
                if (newDeviceId == null)
                {
                    _defaultCommunicationsDevice = null;
                }
                else
                {
                    FindDevice(newDeviceId, out _defaultCommunicationsDevice);
                }
            }
        }

        public IVirtualDefaultAudioDevice VirtualDefaultDevice => _virtualDefaultDevice;

        public IAudioDevice DefaultPlaybackDevice
        {
            get => _defaultPlaybackDevice;
            set
            {
                if (_defaultPlaybackDevice == null ||
                    value.Id != _defaultPlaybackDevice.Id)
                {
                    SetDefaultDevice(value);
                }
            }
        }

        public IAudioDevice DefaultCommunicationDevice
        {
            get => _defaultCommunicationsDevice;
            set
            {
                if (_defaultCommunicationsDevice == null ||
                    value.Id != _defaultCommunicationsDevice.Id)
                {
                    SetDefaultDevice(value, ERole.eCommunications);
                }
            }
        }

        private void SetDefaultDevice(IAudioDevice device, ERole role = ERole.eMultimedia)
        {
            if (s_PolicyConfigClient == null)
            {
                s_PolicyConfigClient = (IPolicyConfig)new PolicyConfigClient();
            }

            s_PolicyConfigClient.SetDefaultEndpoint(device.Id, role);
        }

        private bool FindDevice(string deviceId, out IAudioDevice found)
        {
            found = _devices.FirstOrDefault(d => d.Id == deviceId);
            return found != null;
        }

        void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
        {
            _dispatcher.SafeInvoke(() =>
            {
                if (!FindDevice(pwstrDeviceId, out IAudioDevice unused))
                {
                    try
                    {
                        IMMDevice device = _enumerator.GetDevice(pwstrDeviceId);
                        if (((IMMEndpoint)device).GetDataFlow() == EDataFlow.eRender)
                        {
                            _devices.Add(new SafeAudioDevice(new AudioDevice(device, this, _dispatcher)));
                        }
                    }
                    catch(Exception ex)
                    {
                        // We catch Exception here because IMMDevice::Activate can return E_POINTER/NullReferenceException, as well as other expcetions listed here:
                        // https://docs.microsoft.com/en-us/dotnet/framework/interop/how-to-map-hresults-and-exceptions
                        Debug.WriteLine(ex);
                    }
                }
            });
        }

        void IMMNotificationClient.OnDeviceRemoved(string pwstrDeviceId)
        {
            _dispatcher.SafeInvoke(() =>
            {
                if (FindDevice(pwstrDeviceId, out IAudioDevice dev))
                {
                    _devices.Remove(dev);
                }
            });
        }

        void IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
        {
            _dispatcher.SafeInvoke(() =>
            {
                QueryDefaultPlaybackDevice();
                QueryDefaultCommunicationsDevice();
            });
        }

        void IMMNotificationClient.OnDeviceStateChanged(string pwstrDeviceId, DeviceState dwNewState)
        {
            switch (dwNewState)
            {
                case DeviceState.ACTIVE:
                    ((IMMNotificationClient)this).OnDeviceAdded(pwstrDeviceId);
                    break;
                case DeviceState.DISABLED:
                case DeviceState.NOTPRESENT:
                case DeviceState.UNPLUGGED:
                    ((IMMNotificationClient)this).OnDeviceRemoved(pwstrDeviceId);
                    break;
                default:
                    Debug.WriteLine($"Unknown DEVICE_STATE: {dwNewState}");
                    break;
            }
        }

        void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key)
        {
            if (FindDevice(pwstrDeviceId, out IAudioDevice dev))
            {
                if (PropertyKeys.PKEY_AudioEndPoint_Interface.Equals(key))
                {
                    ((IAudioDeviceInternal)dev).DevicePropertiesChanged(_enumerator.GetDevice(dev.Id));
                }
            }
        }

        public void OnSessionCreated(IAudioDeviceSession session)
        {
            SessionCreated?.Invoke(this, session);
        }
    }
}
