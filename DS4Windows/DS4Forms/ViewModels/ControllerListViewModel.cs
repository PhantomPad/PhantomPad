/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public class ControllerListViewModel
    {
        //private object _colLockobj = new object();
        private ReaderWriterLockSlim _colListLocker = new ReaderWriterLockSlim();
        private ObservableCollection<CompositeDeviceModel> controllerCol =
            new ObservableCollection<CompositeDeviceModel>();
        private Dictionary<int, CompositeDeviceModel> controllerDict =
            new Dictionary<int, CompositeDeviceModel>();

        public ObservableCollection<CompositeDeviceModel> ControllerCol
        { get => controllerCol; set => controllerCol = value; }

        private ProfileList profileListHolder;
        private ControlService controlService;
        private int currentIndex;
        public int CurrentIndex { get => currentIndex; set => currentIndex = value; }
        public CompositeDeviceModel CurrentItem {
            get
            {
                if (currentIndex == -1) return null;
                controllerDict.TryGetValue(currentIndex, out CompositeDeviceModel item);
                return item;
            }
        }

        public Dictionary<int, CompositeDeviceModel> ControllerDict { get => controllerDict; set => controllerDict = value; }

        //public ControllerListViewModel(Tester tester, ProfileList profileListHolder)
        public ControllerListViewModel(ControlService service, ProfileList profileListHolder)
        {
            this.profileListHolder = profileListHolder;
            this.controlService = service;
            service.ServiceStarted += ControllersChanged;
            service.PreServiceStop += ClearControllerList;
            service.HotplugController += Service_HotplugController;
            //tester.StartControllers += ControllersChanged;
            //tester.ControllersRemoved += ClearControllerList;

            int idx = 0;
            foreach (DS4Device currentDev in controlService.slotManager.ControllerColl)
            {
                CompositeDeviceModel temp = new CompositeDeviceModel(currentDev,
                    idx, Global.ProfilePath[idx], profileListHolder);
                controllerCol.Add(temp);
                controllerDict.Add(idx, temp);
                currentDev.Removal += Controller_Removal;
                idx++;
            }

            //BindingOperations.EnableCollectionSynchronization(controllerCol, _colLockobj);
            BindingOperations.EnableCollectionSynchronization(controllerCol, _colListLocker,
                ColLockCallback);

            // PhantomPad: refrescar cada segundo la latencia y la frecuencia de cada tarjeta
            liveStatsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            liveStatsTimer.Tick += (s, e) =>
            {
                List<CompositeDeviceModel> items;
                using (ReadLocker locker = new ReadLocker(_colListLocker))
                {
                    items = new List<CompositeDeviceModel>(controllerCol);
                }

                foreach (CompositeDeviceModel item in items) item.RefreshLiveStats();
            };
            liveStatsTimer.Start();
        }

        private readonly System.Windows.Threading.DispatcherTimer liveStatsTimer;

        private void ColLockCallback(IEnumerable collection, object context,
            Action accessMethod, bool writeAccess)
        {
            if (writeAccess)
            {
                using (WriteLocker locker = new WriteLocker(_colListLocker))
                {
                    accessMethod?.Invoke();
                }
            }
            else
            {
                using (ReadLocker locker = new ReadLocker(_colListLocker))
                {
                    accessMethod?.Invoke();
                }
            }
        }

        private void Service_HotplugController(ControlService sender,
            DS4Device device, int index)
        {
            // Engage write lock pre-maturely
            using (WriteLocker readLock = new WriteLocker(_colListLocker))
            {
                // Look if device exists. Also, check if disconnect might be occurring
                if (!controllerDict.ContainsKey(index) && !device.IsRemoving)
                {
                    CompositeDeviceModel temp = new CompositeDeviceModel(device,
                        index, Global.ProfilePath[index], profileListHolder);
                    controllerCol.Add(temp);
                    controllerDict.Add(index, temp);

                    device.Removal += Controller_Removal;
                }
            }
        }

        private void ClearControllerList(object sender, EventArgs e)
        {
            _colListLocker.EnterReadLock();
            foreach (CompositeDeviceModel temp in controllerCol)
            {
                temp.Device.Removal -= Controller_Removal;
            }
            _colListLocker.ExitReadLock();

            // PhantomPad: quitar las tarjetas una a una (como cuando se desconecta un mando).
            // Vaciar la lista de golpe hacia que WPF rehiciera todas las tarjetas y la lista de
            // perfiles de la tarjeta lanzaba un error que cerraba el programa al pulsar "Detener".
            _colListLocker.EnterWriteLock();
            for (int i = controllerCol.Count - 1; i >= 0; i--)
            {
                controllerCol.RemoveAt(i);
            }

            controllerDict.Clear();
            _colListLocker.ExitWriteLock();
        }

        private void ControllersChanged(object sender, EventArgs e)
        {
            //IEnumerable<DS4Device> devices = DS4Windows.DS4Devices.getDS4Controllers();
            using (ReadLocker locker = new ReadLocker(controlService.slotManager.CollectionLocker))
            {
                foreach (DS4Device currentDev in controlService.slotManager.ControllerColl)
                {
                    bool found = false;
                    _colListLocker.EnterReadLock();
                    foreach (CompositeDeviceModel temp in controllerCol)
                    {
                        if (temp.Device == currentDev)
                        {
                            found = true;
                            break;
                        }
                    }
                    _colListLocker.ExitReadLock();

                    // Check for new device. Also, check if disconnect might be occurring
                    if (!found && !currentDev.IsRemoving)
                    {
                        //int idx = controllerCol.Count;
                        _colListLocker.EnterWriteLock();
                        int idx = controlService.slotManager.ReverseControllerDict[currentDev];
                        CompositeDeviceModel temp = new CompositeDeviceModel(currentDev,
                            idx, Global.ProfilePath[idx], profileListHolder);
                        controllerCol.Add(temp);
                        controllerDict.Add(idx, temp);
                        _colListLocker.ExitWriteLock();

                        currentDev.Removal += Controller_Removal;
                    }
                }
            }
        }

        private void Controller_Removal(object sender, EventArgs e)
        {
            DS4Device currentDev = sender as DS4Device;
            CompositeDeviceModel found = null;
            _colListLocker.EnterReadLock();
            foreach (CompositeDeviceModel temp in controllerCol)
            {
                if (temp.Device == currentDev)
                {
                    found = temp;
                    break;
                }
            }
            _colListLocker.ExitReadLock();

            if (found != null)
            {
                _colListLocker.EnterWriteLock();
                controllerCol.Remove(found);
                controllerDict.Remove(found.DevIndex);
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    Global.Save();
                });
                Global.linkedProfileCheck[found.DevIndex] = false;
                _colListLocker.ExitWriteLock();
            }
        }
    }

    public class CompositeDeviceModel
    {
        private DS4Device device;
        private string selectedProfile;
        private ProfileList profileListHolder;
        private ProfileEntity selectedEntity;
        private int selectedIndex = -1;
        private int devIndex;

        public DS4Device Device { get => device; set => device = value; }
        public string SelectedProfile { get => selectedProfile; set => selectedProfile = value; }
        public ProfileList ProfileEntities { get => profileListHolder; set => profileListHolder = value; }
        public ObservableCollection<ProfileEntity> ProfileListCol => profileListHolder.ProfileListCol;

        public string LightColor
        {
            get
            {
                DS4Color color;
                if (Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed)
                {
                    color = Global.LightbarSettingsInfo[devIndex].ds4winSettings.m_CustomLed; //Global.CustomColor[devIndex];
                }
                else
                {
                    color = Global.LightbarSettingsInfo[devIndex].ds4winSettings.m_Led;
                }
                return $"#FF{color.red.ToString("X2")}{color.green.ToString("X2")}{color.blue.ToString("X2")}";
            }
        }

        public event EventHandler LightColorChanged;

        public Color CustomLightColor
        {
            get
            {
                DS4Color color;
                color = Global.LightbarSettingsInfo[devIndex].ds4winSettings.m_CustomLed;
                return new Color() { R = color.red, G = color.green, B = color.blue, A = 255 };
            }
        }

        public string BatteryState
        {
            get
            {
                string temp = $"{device.Battery}%{(device.Charging ? "+" : "")}";
                return temp;
            }
        }
        public event EventHandler BatteryStateChanged;

        public int SelectedIndex
        {
            get => selectedIndex;
            set
            {
                if (selectedIndex == value) return;
                selectedIndex = value;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler SelectedIndexChanged;

        public string StatusSource
        {
            get
            {
                string imgName = (string)App.Current.FindResource(device.ConnectionType == ConnectionType.USB ? "UsbImg" : "BtImg");
                string source = $"{Global.RESOURCES_PREFIX}/{imgName}";
                return source;
            }
        }

        public string ExclusiveSource
        {
            get
            {
                string imgName = (string)App.Current.FindResource("CancelImg");
                string source = $"{Global.RESOURCES_PREFIX}/{imgName}";
                switch(device.CurrentExclusiveStatus)
                {
                    case DS4Device.ExclusiveStatus.Exclusive:
                        imgName = (string)App.Current.FindResource("CheckedImg");
                        source = $"{Global.RESOURCES_PREFIX}/{imgName}";
                        break;
                    case DS4Device.ExclusiveStatus.HidHideAffected:
                    case DS4Device.ExclusiveStatus.HidGuardAffected:
                        imgName = (string)App.Current.FindResource("KeyImageImg");
                        source = $"{Global.RESOURCES_PREFIX}/{imgName}";
                        break;
                    default:
                        break;
                }

                return source;
            }
        }

        public bool LinkedProfile
        {
            get
            {
                return Global.linkedProfileCheck[devIndex];
            }
            set
            {
                bool temp = Global.linkedProfileCheck[devIndex];
                if (temp == value) return;
                Global.linkedProfileCheck[devIndex] = value;
                SaveLinked(value);
            }
        }

        public int DevIndex { get => devIndex; }
        public int DisplayDevIndex { get => devIndex + 1; }

        // PhantomPad: datos separados para la tarjeta grande del mando
        public int BatteryLevel { get => device.Battery; }
        public event EventHandler BatteryLevelChanged;
        public string DeviceName { get => device.DisplayName; }
        public string MacText { get => device.MacAddress; }
        public event EventHandler MacTextChanged;
        public string ConnectionText
        {
            get => device.ConnectionType switch
            {
                ConnectionType.USB => "USB",
                ConnectionType.BT => "Bluetooth",
                ConnectionType.SONYWA => "Sony Wireless Adapter",
                _ => device.ConnectionType.ToString(),
            };
        }

        public string TooltipIDText
        {
            get
            {
                string temp = string.Format(Properties.Resources.InputDelay, device.Latency);
                return temp;
            }
        }

        public event EventHandler TooltipIDTextChanged;

        private bool useCustomColor;
        public bool UseCustomColor { get => useCustomColor; set => useCustomColor = value; }

        private ContextMenu lightContext;
        public ContextMenu LightContext { get => lightContext; set => lightContext = value; }

        public string IdText
        {
            get => $"{device.DisplayName} ({device.MacAddress})";
        }
        public event EventHandler IdTextChanged;

        public string IsExclusiveText
        {
            get
            {
                string temp = Translations.Strings.SharedAccess;
                switch(device.CurrentExclusiveStatus)
                {
                    case DS4Device.ExclusiveStatus.Exclusive:
                        temp = Translations.Strings.ExclusiveAccess;
                        break;
                    case DS4Device.ExclusiveStatus.HidHideAffected:
                        temp = Translations.Strings.HidHideAccess;
                        break;
                    case DS4Device.ExclusiveStatus.HidGuardAffected:
                        temp = Translations.Strings.HidGuardianAccess;
                        break;
                    default:
                        break;
                }

                return temp;
            }
        }

        public bool PrimaryDevice
        {
            get => device.PrimaryDevice;
        }

        public delegate void CustomColorHandler(CompositeDeviceModel sender);
        public event CustomColorHandler RequestColorPicker;

        public CompositeDeviceModel(DS4Device device, int devIndex, string profile,
            ProfileList collection)
        {
            this.device = device;
            device.BatteryChanged += (sender, e) => BatteryStateChanged?.Invoke(this, e);
            device.ChargingChanged += (sender, e) => BatteryStateChanged?.Invoke(this, e);
            device.MacAddressChanged += (sender, e) => IdTextChanged?.Invoke(this, e);
            // PhantomPad: avisar a la tarjeta del mando
            device.BatteryChanged += (sender, e) => BatteryLevelChanged?.Invoke(this, e);
            device.MacAddressChanged += (sender, e) => MacTextChanged?.Invoke(this, e);
            this.devIndex = devIndex;
            this.selectedProfile = profile;
            profileListHolder = collection;
            if (!string.IsNullOrEmpty(selectedProfile))
            {
                this.selectedEntity = profileListHolder.ProfileListCol.SingleOrDefault(x => x.Name == selectedProfile);
            }

            if (this.selectedEntity != null)
            {
                selectedIndex = profileListHolder.ProfileListCol.IndexOf(this.selectedEntity);
                HookEvents(true);
            }

            useCustomColor = Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed;
        }

        public void ChangeSelectedProfile()
        {
            if (selectedIndex == -1)
            {
                return;
            }

            if (this.selectedEntity != null)
            {
                HookEvents(false);
            }

            string prof = Global.ProfilePath[devIndex] = ProfileListCol[selectedIndex].Name;
            if (LinkedProfile)
            {
                Global.changeLinkedProfile(device.getMacAddress(), Global.ProfilePath[devIndex]);
                Global.SaveLinkedProfiles();
            }
            else
            {
                Global.OlderProfilePath[devIndex] = Global.ProfilePath[devIndex];
            }

            //Global.Save();
            // Run profile loading in Task. Need to still wait for Task to finish
            Task.Run(() =>
            {
                if (device != null)
                {
                    device.HaltReportingRunAction(() =>
                    {
                        Global.LoadProfile(devIndex, true, App.rootHub);
                    });
                }

            }).Wait();

            string prolog = string.Format(Properties.Resources.UsingProfile, (devIndex + 1).ToString(), prof, $"{device.Battery}");
            DS4Windows.AppLogger.LogToGui(prolog, false);

            selectedProfile = prof;
            this.selectedEntity = profileListHolder.ProfileListCol.SingleOrDefault(x => x.Name == prof);
            if (this.selectedEntity != null)
            {
                selectedIndex = profileListHolder.ProfileListCol.IndexOf(this.selectedEntity);
                HookEvents(true);
            }

            LightColorChanged?.Invoke(this, EventArgs.Empty);
        }

        private void HookEvents(bool state)
        {
            if (state)
            {
                selectedEntity.ProfileSaved += SelectedEntity_ProfileSaved;
                selectedEntity.ProfileDeleted += SelectedEntity_ProfileDeleted;
            }
            else
            {
                selectedEntity.ProfileSaved -= SelectedEntity_ProfileSaved;
                selectedEntity.ProfileDeleted -= SelectedEntity_ProfileDeleted;
            }
        }

        private void SelectedEntity_ProfileDeleted(object sender, EventArgs e)
        {
            HookEvents(false);
            ProfileEntity entity = profileListHolder.ProfileListCol.FirstOrDefault();
            if (entity != null)
            {
                SelectedIndex = profileListHolder.ProfileListCol.IndexOf(entity);
            }
        }

        private void SelectedEntity_ProfileSaved(object sender, EventArgs e)
        {
            // Run profile loading in Task. Need to still wait for Task to finish
            Task.Run(() =>
            {
                device.HaltReportingRunAction(() =>
                {
                    Global.LoadProfile(devIndex, false, App.rootHub);
                });
            }).Wait();

            LightColorChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RequestUpdatedTooltipID()
        {
            TooltipIDTextChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SaveLinked(bool status)
        {
            if (device != null && device.isSynced())
            {
                if (status)
                {
                    if (device.isValidSerial())
                    {
                        Global.changeLinkedProfile(device.getMacAddress(), Global.ProfilePath[devIndex]);
                    }
                }
                else
                {
                    Global.removeLinkedProfile(device.getMacAddress());
                    Global.ProfilePath[devIndex] = Global.OlderProfilePath[devIndex];
                }

                Global.SaveLinkedProfiles();
            }
        }

        public void AddLightContextItems()
        {
            MenuItem thing = new MenuItem() { Header = PPText.Name("Use Profile Color"), IsChecked = !useCustomColor };
            thing.Click += ProfileColorMenuClick;
            lightContext.Items.Add(thing);
            thing = new MenuItem() { Header = PPText.Name("Use Custom Color"), IsChecked = useCustomColor };
            thing.Click += CustomColorItemClick;
            lightContext.Items.Add(thing);
        }

        private void ProfileColorMenuClick(object sender, System.Windows.RoutedEventArgs e)
        {
            useCustomColor = false;
            RefreshLightContext();
            Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed = false;
            LightColorChanged?.Invoke(this, EventArgs.Empty);
            UseCustomLightColorChanged?.Invoke(this, EventArgs.Empty);
        }

        private void CustomColorItemClick(object sender, System.Windows.RoutedEventArgs e)
        {
            useCustomColor = true;
            RefreshLightContext();
            Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed = true;
            LightColorChanged?.Invoke(this, EventArgs.Empty);
            UseCustomLightColorChanged?.Invoke(this, EventArgs.Empty);
            RequestColorPicker?.Invoke(this);
        }

        private void RefreshLightContext()
        {
            (lightContext.Items[0] as MenuItem).IsChecked = !useCustomColor;
            (lightContext.Items[1] as MenuItem).IsChecked = useCustomColor;
        }

        public void UpdateCustomLightColor(Color color)
        {
            Global.LightbarSettingsInfo[devIndex].ds4winSettings.m_CustomLed = new DS4Color() { red = color.R, green = color.G, blue = color.B };
            PhantomDevice.Capture(devIndex); // PhantomPad: el color queda guardado en este mando
            LightColorChanged?.Invoke(this, EventArgs.Empty);
        }

        // ===== PhantomPad: control de luz desde la tarjeta del mando =====

        /// <summary>Luz encendida o apagada (se guarda por mando).</summary>
        public bool LightEnabled
        {
            get => PhantomLight.IsEnabled(devIndex);
            set
            {
                if (PhantomLight.IsEnabled(devIndex) == value) return;
                PhantomLight.SetEnabled(devIndex, value);
                LightEnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler LightEnabledChanged;

        /// <summary>Brillo de 0 a 100 % (se guarda por mando).</summary>
        public double LightBrightness
        {
            get => PhantomLight.GetBrightness(devIndex);
            set
            {
                int level = (int)Math.Round(value);
                if (PhantomLight.GetBrightness(devIndex) == level) return;
                PhantomLight.SetBrightness(devIndex, level);
                LightBrightnessChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler LightBrightnessChanged;

        /// <summary>true = color elegido aqui; false = color definido en el perfil.</summary>
        public bool UseCustomLightColor
        {
            get => Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed;
            set
            {
                if (Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed == value) return;
                useCustomColor = value;
                Global.LightbarSettingsInfo[devIndex].ds4winSettings.useCustomLed = value;
                PhantomDevice.Capture(devIndex);
                if (lightContext != null && lightContext.Items.Count >= 2) RefreshLightContext();
                UseCustomLightColorChanged?.Invoke(this, EventArgs.Empty);
                LightColorChanged?.Invoke(this, EventArgs.Empty);
                Global.Save();
            }
        }
        public event EventHandler UseCustomLightColorChanged;

        /// <summary>Elegir un color rapido: activa el color personalizado con ese color.</summary>
        public void SetQuickLightColor(Color color)
        {
            UpdateCustomLightColor(color);
            if (!UseCustomLightColor)
            {
                UseCustomLightColor = true; // ya guarda la configuracion
            }
            else
            {
                Global.Save();
            }

            if (!LightEnabled)
            {
                LightEnabled = true;
            }
        }

        // ===== PhantomPad: Ajustes del mando (gyro aim global) =====

        /// <summary>Gyro aim en todos los juegos, al apuntar con L2.</summary>
        public bool GyroEnabled
        {
            get => PhantomGyro.IsEnabled(devIndex);
            set
            {
                if (PhantomGyro.IsEnabled(devIndex) == value) return;
                PhantomGyro.SetEnabled(devIndex, value);
                // Al apagarlo se recarga el perfil para volver a su propio ajuste de giroscopio
                if (!value) ReloadCurrentProfile();

                GyroEnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler GyroEnabledChanged;

        /// <summary>Vuelve a cargar el perfil que usa ahora este mando (normal o el del juego).</summary>
        private void ReloadCurrentProfile(Action afterReload = null)
        {
            int index = devIndex;
            DS4Device dev = device;
            // En segundo plano: la pausa del mando puede no llegar a tiempo, asi que se reintenta
            Task.Run(() =>
            {
                bool ran = false;
                Action reload = () =>
                {
                    if (Global.useTempProfile[index])
                        Global.LoadTempProfile(index, Global.tempprofilename[index], false, App.rootHub);
                    else
                        Global.LoadProfile(index, false, App.rootHub);
                    ran = true;
                };

                for (int attempt = 0; attempt < 6 && !ran; attempt++)
                {
                    if (dev != null) dev.HaltReportingRunAction(reload);
                    else reload();
                }

                if (!ran)
                {
                    AppLogger.LogToGui(PPText.T("PPC_ReloadFailed", "Controller {0}: the change could not be applied now. Try again.", index + 1), true);
                }

                if (afterReload != null) System.Windows.Application.Current?.Dispatcher.BeginInvoke(afterReload);
            });
        }

        // ===== PhantomPad: modo compatible y frecuencia de lectura =====

        // Opciones de la lista desplegable como texto simple (los textos traducidos con lex:Loc
        // dentro de una plantilla hacian caer el programa al quitar la tarjeta de un mando)

        public List<string> PollOptions { get; } = new List<string>
        {
            PPText.T("PP_PollAuto", "Automatic"), "1000 Hz (1 ms)", "500 Hz (2 ms)", "250 Hz (4 ms)", "125 Hz (8 ms)",
        };


        /// <summary>Solo los mandos DS4 tienen el modo compatible para mandos no originales.</summary>
        public bool HasCompatMode => PhantomDevice.GetCompatMode(device).HasValue;

        /// <summary>Los mandos de Xbox no tienen luz ni giroscopio: esas secciones no se muestran.</summary>
        public bool HasLightAndGyro => device?.DeviceType != DS4Windows.InputDevices.InputDeviceType.Xbox;

        /// <summary>Modo compatible para mandos no originales (cambia como se envian luz y vibracion).</summary>
        public bool CompatMode
        {
            get => PhantomDevice.GetCompatMode(device) ?? false;
            set
            {
                if (CompatMode == value) return;
                PhantomDevice.SetCompatMode(device, value);
                CompatModeChanged?.Invoke(this, EventArgs.Empty);
                LightColorChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler CompatModeChanged;

        /// <summary>Opcion de frecuencia elegida (indice de PhantomDevice.PollChoices; 0 = automatica).</summary>
        public int PollChoiceIndex
        {
            get => Math.Max(0, Array.IndexOf(PhantomDevice.PollChoices, PhantomDevice.GetPollChoice(devIndex)));
            set
            {
                if (value < 0 || value >= PhantomDevice.PollChoices.Length || value == PollChoiceIndex) return;
                PhantomDevice.SetPollChoice(devIndex, PhantomDevice.PollChoices[value]);
                PollChoiceIndexChanged?.Invoke(this, EventArgs.Empty);
                PollRateTextChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler PollChoiceIndexChanged;

        /// <summary>Solo los DS4 por Bluetooth aceptan que se les pida la frecuencia.</summary>
        public bool CanSetPollRate => PhantomDevice.CanSetPollRate(device);

        /// <summary>Frecuencia que se le pide al mando, explicada.</summary>
        public string PollRateText
        {
            get
            {
                if (!CanSetPollRate)
                {
                    return device?.ConnectionType == ConnectionType.USB
                        ? PPText.T("PP_PollUsb", "USB cable: the controller sets its own rate.")
                        : PPText.T("PP_PollFixed", "This controller sets its own rate.");
                }

                int ms = PhantomDevice.EffectivePollMs(devIndex);
                int hz = 1000 / ms;
                if (PhantomDevice.GetPollChoice(devIndex) > 0)
                    return PPText.T("PP_PollManual", "Requested: {0} Hz (every {1} ms).", hz, ms);
                int count = PhantomDevice.BluetoothCount();
                return count <= 1
                    ? PPText.T("PP_PollAutoSingle", "Automatic: {0} Hz, 100 % for the only controller.", hz)
                    : PPText.T("PP_PollAutoMulti", "Automatic: {0} Hz for each of the {1} controllers.", hz, count);
            }
        }
        public event EventHandler PollRateTextChanged;

        /// <summary>Lectura real: tiempo medio entre lecturas que llegan del mando.</summary>
        public string LatencyText
        {
            get
            {
                double latency = device?.Latency ?? 0;
                if (latency <= 0.05) return "—";
                return PPText.T("PP_LatencyValue", "{0} ms between readings (≈ {1} Hz)",
                    latency.ToString("0.0"), (1000.0 / latency).ToString("0"));
            }
        }
        public event EventHandler LatencyTextChanged;

        /// <summary>Lo llama cada segundo la lista de mandos para refrescar los datos en vivo.</summary>
        public void RefreshLiveStats()
        {
            LatencyTextChanged?.Invoke(this, EventArgs.Empty);
            PollRateTextChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Sensibilidad del gyro aim en % (100 = normal).</summary>
        public double GyroSensitivity
        {
            get => PhantomGyro.GetSensitivity(devIndex);
            set
            {
                int level = (int)Math.Round(value);
                if (PhantomGyro.GetSensitivity(devIndex) == level) return;
                PhantomGyro.SetSensitivity(devIndex, level);
                GyroSensitivityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler GyroSensitivityChanged;

        /// <summary>Pausar el gyro en juegos de carreras (alli L2 es el freno). Es igual para todos los mandos.</summary>
        public bool GyroPauseInRacing
        {
            get => PhantomGyro.PauseInRacing;
            set
            {
                if (PhantomGyro.PauseInRacing == value) return;
                PhantomGyro.PauseInRacing = value;
                GyroPauseInRacingChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler GyroPauseInRacingChanged;

        /// <summary>Pide abrir el selector de color completo.</summary>
        public void OpenColorPicker()
        {
            if (!UseCustomLightColor)
            {
                UseCustomLightColor = true;
            }

            RequestColorPicker?.Invoke(this);
        }

        public void ChangeSelectedProfile(string loadprofile)
        {
            ProfileEntity temp = profileListHolder.ProfileListCol.SingleOrDefault(x => x.Name == loadprofile);
            if (temp != null)
            {
                SelectedIndex = profileListHolder.ProfileListCol.IndexOf(temp);
            }
        }

        public void RequestDisconnect()
        {
            if (device.Synced && !device.Charging)
            {
                if (device.ConnectionType == ConnectionType.BT)
                {
                    //device.StopUpdate();
                    device.queueEvent(() =>
                    {
                        device.DisconnectBT();
                    });
                }
                else if (device.ConnectionType == ConnectionType.SONYWA)
                {
                    device.DisconnectDongle();
                }
            }
        }
    }
}
