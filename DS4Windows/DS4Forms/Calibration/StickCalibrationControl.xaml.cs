/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.Calibration
{
    /// <summary>
    /// PhantomPad: asistente de calibracion de sticks (anti-drift).
    /// Paso 1: sin tocar el mando se mide cuanto se mueve solo cada stick (deriva).
    /// Paso 2: girando los sticks en circulos se mide hasta donde llegan (alcance).
    /// El resultado se guarda en el mando (por su MAC) y se aplica encima de cualquier perfil.
    /// </summary>
    public partial class StickCalibrationControl : UserControl
    {
        private enum Step { Idle, Rest, Circle, Done }

        private const double CANVAS_CENTER = 80.0;
        private const double CANVAS_RADIUS = 70.0;   // px que equivalen a un stick al borde (127)
        private const double REST_SECONDS = 3.0;
        private const double CIRCLE_SECONDS = 6.0;
        private const int SAFETY_MARGIN = 3;          // unidades extra de zona muerta (de 127)
        private const int SECTORS = 8;

        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        private readonly DS4State state = new DS4State();
        private readonly List<int> slots = new List<int>();
        private string listSignature = "";
        private int tickCount;

        private Step step = Step.Idle;
        private DateTime stepStart;
        private double driftL, driftR;
        private readonly double[] reachL = new double[SECTORS], reachR = new double[SECTORS];
        private PhantomDevice.StickCalibration pending;

        public StickCalibrationControl()
        {
            InitializeComponent();
            timer.Tick += Timer_Tick;
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { RefreshControllers(force: true); timer.Start(); }
                else { timer.Stop(); ResetWizard(); }
            };
            ResetWizard();
        }

        private int SelectedSlot => controllerCombo.SelectedIndex >= 0 && controllerCombo.SelectedIndex < slots.Count
            ? slots[controllerCombo.SelectedIndex] : -1;

        private DS4Device SelectedDevice
        {
            get
            {
                int slot = SelectedSlot;
                return slot >= 0 ? Program.rootHub?.DS4Controllers[slot] : null;
            }
        }

        // ===================== Lista de mandos =====================

        private void RefreshControllers(bool force = false)
        {
            var newSlots = new List<int>();
            var names = new List<string>();
            for (int i = 0; i < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; i++)
            {
                DS4Device dev = Program.rootHub?.DS4Controllers[i];
                if (dev == null || !dev.PrimaryDevice) continue;
                newSlots.Add(i);
                names.Add(PPText.T("PP_CalControllerItem", "Controller {0} · {1} · {2}", i + 1, dev.DisplayName, dev.getMacAddress()));
            }

            string signature = string.Join("|", names);
            if (!force && signature == listSignature) return;
            listSignature = signature;

            int previous = SelectedSlot;
            slots.Clear();
            slots.AddRange(newSlots);
            controllerCombo.ItemsSource = names;
            int keep = slots.IndexOf(previous);
            controllerCombo.SelectedIndex = keep >= 0 ? keep : (slots.Count > 0 ? 0 : -1);
            if (step != Step.Idle && keep < 0) ResetWizard();
            UpdateSavedText();
            startBtn.IsEnabled = slots.Count > 0;
            if (slots.Count == 0)
            {
                stepTitle.Text = PPText.T("PP_CalNoController", "No controller connected");
                stepText.Text = PPText.T("PP_CalNoControllerText", "Turn on a controller to calibrate it.");
            }
            else if (step == Step.Idle)
            {
                ShowIdleText();
            }
        }

        private void ControllerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (step != Step.Idle) ResetWizard();
            UpdateSavedText();
        }

        private void UpdateSavedText()
        {
            int slot = SelectedSlot;
            PhantomDevice.StickCalibration cal = slot >= 0 ? PhantomDevice.GetCalibration(slot) : null;
            if (cal == null || !cal.IsSet)
            {
                savedText.Text = PPText.T("PP_CalNotSaved", "This controller has no calibration yet.");
                clearBtn.Visibility = Visibility.Collapsed;
                return;
            }

            savedText.Text = PPText.T("PP_CalSavedInfo", "Saved calibration ({0}): dead zone left {1} %, right {2} % (anti-drift on).",
                cal.Date ?? "-", Pct(cal.LSDeadZone), Pct(cal.RSDeadZone));
            clearBtn.Visibility = Visibility.Visible;
        }

        // ===================== Asistente =====================

        private void ShowIdleText()
        {
            stepTitle.Text = PPText.T("PP_CalIdleTitle", "Ready to calibrate");
            stepText.Text = PPText.T("PP_CalIdleText",
                "It takes about 10 seconds: first leave the sticks alone, then turn them in full circles.");
        }

        private void ResetWizard()
        {
            step = Step.Idle;
            pending = null;
            stepProgress.Visibility = Visibility.Collapsed;
            resultText.Visibility = Visibility.Collapsed;
            saveBtn.Visibility = Visibility.Collapsed;
            startBtn.IsEnabled = slots.Count > 0;
            ((TextBlock)startBtn.Content).Text = PPText.T("PP_CalStart", "Start calibration");
            ShowIdleText();
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDevice == null) return;
            driftL = driftR = 0;
            Array.Clear(reachL, 0, SECTORS);
            Array.Clear(reachR, 0, SECTORS);
            pending = null;
            step = Step.Rest;
            stepStart = DateTime.UtcNow;
            startBtn.IsEnabled = false;
            saveBtn.Visibility = Visibility.Collapsed;
            resultText.Visibility = Visibility.Collapsed;
            stepProgress.Visibility = Visibility.Visible;
            stepTitle.Text = PPText.T("PP_CalStep1Title", "Step 1 of 2: don't touch the sticks");
            stepText.Text = PPText.T("PP_CalStep1Text", "Leave the controller on the table or let go of both sticks. Measuring how much they move on their own...");
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            int slot = SelectedSlot;
            if (slot < 0 || pending == null) return;
            PhantomDevice.SetCalibration(slot, pending);
            UpdateSavedText();
            ResetWizard();
            stepTitle.Text = PPText.T("PP_CalSavedTitle", "Calibration saved");
            stepText.Text = PPText.T("PP_CalSavedText", "It is applied now in every game and profile for this controller.");
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            int slot = SelectedSlot;
            if (slot < 0) return;
            PhantomDevice.SetCalibration(slot, null);
            UpdateSavedText();
            ResetWizard();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (++tickCount % 33 == 0) RefreshControllers(); // cada ~1 s
            DS4Device dev = SelectedDevice;
            if (dev == null) return;

            dev.getRawCurrentState(state);
            double lx = state.LX - 128.0, ly = state.LY - 128.0, rx = state.RX - 128.0, ry = state.RY - 128.0;
            double lMag = Math.Sqrt(lx * lx + ly * ly), rMag = Math.Sqrt(rx * rx + ry * ry);

            DrawStick(lsDot, lsDead, lsReach, lx, ly, Math.Max(Global.LSModInfo[SelectedSlot].deadZone, pending?.LSDeadZone ?? 0), PreviewMax(Global.LSModInfo[SelectedSlot].maxZone, pending?.LSMaxZone ?? 0));
            DrawStick(rsDot, rsDead, rsReach, rx, ry, Math.Max(Global.RSModInfo[SelectedSlot].deadZone, pending?.RSDeadZone ?? 0), PreviewMax(Global.RSModInfo[SelectedSlot].maxZone, pending?.RSMaxZone ?? 0));
            lsText.Text = PPText.T("PP_CalPosition", "Position: {0} %", Pct(lMag));
            rsText.Text = PPText.T("PP_CalPosition", "Position: {0} %", Pct(rMag));

            double elapsed = (DateTime.UtcNow - stepStart).TotalSeconds;
            switch (step)
            {
                case Step.Rest:
                    driftL = Math.Max(driftL, lMag);
                    driftR = Math.Max(driftR, rMag);
                    stepProgress.Value = Math.Min(100, elapsed / REST_SECONDS * 100);
                    if (elapsed >= REST_SECONDS)
                    {
                        step = Step.Circle;
                        stepStart = DateTime.UtcNow;
                        stepTitle.Text = PPText.T("PP_CalStep2Title", "Step 2 of 2: turn both sticks in circles");
                        stepText.Text = PPText.T("PP_CalStep2Text", "Push them to the edge and turn them slowly in full circles, both sticks at the same time.");
                    }
                    break;

                case Step.Circle:
                    TrackReach(reachL, lx, ly, lMag);
                    TrackReach(reachR, rx, ry, rMag);
                    stepProgress.Value = Math.Min(100, elapsed / CIRCLE_SECONDS * 100);
                    if (elapsed >= CIRCLE_SECONDS) Finish();
                    break;
            }
        }

        private static void TrackReach(double[] reach, double x, double y, double mag)
        {
            if (mag < 40) return; // solo cuenta cuando el stick esta bien empujado
            double angle = Math.Atan2(y, x);
            int sector = (int)Math.Floor((angle + Math.PI) / (2 * Math.PI) * SECTORS) % SECTORS;
            reach[sector] = Math.Max(reach[sector], mag);
        }

        private void Finish()
        {
            step = Step.Done;
            stepProgress.Visibility = Visibility.Collapsed;

            int dzL = DeadZoneFor(driftL), dzR = DeadZoneFor(driftR);
            int maxL = MaxZoneFor(reachL, out bool fullL), maxR = MaxZoneFor(reachR, out bool fullR);
            pending = new PhantomDevice.StickCalibration { LSDeadZone = dzL, RSDeadZone = dzR, LSMaxZone = maxL, RSMaxZone = maxR };

            stepTitle.Text = PPText.T("PP_CalDoneTitle", "Result");
            stepText.Text = PPText.T("PP_CalDoneText", "Check it and press Save to use it in every game.");
            var lines = new List<string>
            {
                "• " + PPText.T("PP_CalResultStick", "{0}: moved on its own {1} % → anti-drift dead zone {2} %.",
                    PPText.T("PP_CalLeftStick", "Left stick"), Pct(driftL), Pct(dzL)),
                ReachLine(maxL, fullL),
                "• " + PPText.T("PP_CalResultStick", "{0}: moved on its own {1} % → anti-drift dead zone {2} %.",
                    PPText.T("PP_CalRightStick", "Right stick"), Pct(driftR), Pct(dzR)),
                ReachLine(maxR, fullR),
            };
            resultText.Text = string.Join(Environment.NewLine, lines);
            resultText.Visibility = Visibility.Visible;
            saveBtn.Visibility = Visibility.Visible;
            startBtn.IsEnabled = true;
            ((TextBlock)startBtn.Content).Text = PPText.T("PP_CalRetry", "Repeat");
        }

        private static string ReachLine(int maxZone, bool measured)
        {
            if (!measured) return "• " + PPText.T("PP_CalReachNotMeasured", "Reach: not measured (turn the stick in full circles to measure it).");
            return maxZone == 0
                ? "• " + PPText.T("PP_CalReachOk", "Reach: reaches the edge well.")
                : "• " + PPText.T("PP_CalReachShort", "Reach: only reaches {0} % → full output from there.", maxZone);
        }

        /// <summary>Zona muerta = lo que se movio solo + un pequeno margen (como maximo 40 %).</summary>
        private static int DeadZoneFor(double drift) =>
            Math.Clamp((int)Math.Ceiling(drift) + SAFETY_MARGIN, 0, 51);

        /// <summary>Alcance en % (0 = llega bien al borde). Necesita haber girado casi todo el circulo.</summary>
        private static int MaxZoneFor(double[] reach, out bool measured)
        {
            int seen = 0;
            double min = double.MaxValue;
            foreach (double r in reach)
            {
                if (r <= 0) continue;
                seen++;
                min = Math.Min(min, r);
            }

            measured = seen >= SECTORS - 2;
            if (!measured) return 0;
            int pct = (int)Math.Floor(min / 127.0 * 100.0);
            return pct >= 97 ? 0 : Math.Clamp(pct, 70, 96);
        }

        private static string Pct(double raw) => Math.Round(raw / 127.0 * 100.0).ToString("0");

        /// <summary>Alcance que se vera al guardar (el calibrado solo puede acortarlo).</summary>
        private static int PreviewMax(int current, int calibrated) => calibrated > 0 ? Math.Min(current, calibrated) : current;

        private static void DrawStick(Ellipse dot, Ellipse dead, Ellipse reach, double x, double y, int deadZone, int maxZone)
        {
            double k = CANVAS_RADIUS / 127.0;
            Canvas.SetLeft(dot, CANVAS_CENTER + Math.Clamp(x, -127, 127) * k - dot.Width / 2);
            Canvas.SetTop(dot, CANVAS_CENTER + Math.Clamp(y, -127, 127) * k - dot.Height / 2);

            double dz = Math.Max(2, deadZone * k);
            dead.Width = dead.Height = dz * 2;
            Canvas.SetLeft(dead, CANVAS_CENTER - dz);
            Canvas.SetTop(dead, CANVAS_CENTER - dz);

            double rr = CANVAS_RADIUS * Math.Clamp(maxZone, 10, 100) / 100.0;
            reach.Width = reach.Height = rr * 2;
            Canvas.SetLeft(reach, CANVAS_CENTER - rr);
            Canvas.SetTop(reach, CANVAS_CENTER - rr);
        }
    }
}
