using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Microsoft.Win32;
using System.Text.Json;
using NAudio.CoreAudioApi;


namespace Akai
{
    public partial class MainWindow : Window
    {
        // 16 Pad için dosya yolları ve önbelleğe alınmış ses verileri
        private readonly string[] _audioFilePaths = new string[16];
        private readonly CachedSound[] _cachedSounds = new CachedSound[16];

        private VolumeSampleProvider _volumeProvider;

        // Düşük gecikmeli WASAPI mikseri
        private IWavePlayer _waveOut;
        private MixingSampleProvider _mixer;

        // Klavyeden parmak davulu (Finger Drumming) tuş haritası:
        // Pad 1-4:   1, 2, 3, 4
        // Pad 5-8:   Q, W, E, R
        // Pad 9-12:  A, S, D, F
        // Pad 13-16: Z, X, C, V
        private readonly Key[] _keyMap =
        {
            Key.D1, Key.D2, Key.D3, Key.D4,
            Key.Q,  Key.W,  Key.E,  Key.R,
            Key.A,  Key.S,  Key.D,  Key.F,
            Key.Z,  Key.X,  Key.C,  Key.V
        };

        public MainWindow()
        {
            InitializeComponent();
            InitAudioEngine();
            SetupPadEvents();
        }

        private readonly List<MMDevice> _outputDevices = new List<MMDevice>();

        private void InitAudioEngine()
        {
            // Mikser ve ortak ses formatı
            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            _mixer = new MixingSampleProvider(waveFormat) { ReadFully = true };
            _volumeProvider = new VolumeSampleProvider(_mixer) { Volume = 0.8f };

            // Aktif ses aygıtlarını tara ve kutuya doldur
            PopulateAudioDevices();
        }

        private void PopulateAudioDevices()
        {
            _outputDevices.Clear();
            AudioDeviceComboBox.Items.Clear();

            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            MMDevice defaultDevice = null;
            try
            {
                defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch { }

            int defaultIndex = 0;
            int index = 0;

            foreach (var device in devices)
            {
                _outputDevices.Add(device);
                AudioDeviceComboBox.Items.Add(device.FriendlyName);

                if (defaultDevice != null && device.ID == defaultDevice.ID)
                {
                    defaultIndex = index;
                }
                index++;
            }

            if (AudioDeviceComboBox.Items.Count > 0)
            {
                AudioDeviceComboBox.SelectedIndex = defaultIndex;
            }
        }

        private void AudioDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = AudioDeviceComboBox.SelectedIndex;
            if (index >= 0 && index < _outputDevices.Count)
            {
                SwitchAudioDevice(_outputDevices[index]);
            }
        }

        private void SwitchAudioDevice(MMDevice device)
        {
            try
            {
                // Önceki ses hattını kapat
                if (_waveOut != null)
                {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                }

                // Seçilen cihaza 20ms düşük gecikmeyle bağlan
                var wasapi = new WasapiOut(device, AudioClientShareMode.Shared, false, 20);
                wasapi.Init(_volumeProvider);
                wasapi.Play();
                _waveOut = wasapi;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ses çıkış aygıtı başlatılamadı: {ex.Message}");
            }
        }

        private void SetupPadEvents()
        {
            for (int i = 0; i < 16; i++)
            {
                int index = i;
                Button pad = (Button)FindName($"Pad{index + 1}");
                if (pad == null) continue;

                // Tıklama olayı
                pad.Click += (s, e) => TriggerPad(index);

                // Dosya sürükleyip bırakma olayları
                pad.Drop += (s, e) =>
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    {
                        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                        if (files.Length > 0 && (files[0].ToLower().EndsWith(".wav") || files[0].ToLower().EndsWith(".mp3")))
                        {
                            LoadSampleToPad(index, files[0]);
                        }
                    }
                };

                pad.DragOver += (s, e) =>
                {
                    e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                    e.Handled = true;
                };
            }

            // Klavye basışlarını dinle
            this.KeyDown += MainWindow_KeyDown;
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            for (int i = 0; i < _keyMap.Length; i++)
            {
                if (e.Key == _keyMap[i])
                {
                    TriggerPad(i);
                    break;
                }
            }
        }

        private void LoadSampleToPad(int index, string path)
        {
            try
            {
                _audioFilePaths[index] = path;
                _cachedSounds[index] = new CachedSound(path);

                Button pad = (Button)FindName($"Pad{index + 1}");
                if (pad != null)
                {
                    // Pad'in üzerine dosya adını ve kısayol tuşunu yazdır
                    string keyName = _keyMap[index].ToString();
                    string shortcut = (keyName.StartsWith("D") && keyName.Length == 2) ? keyName.Substring(1) : keyName;
                    pad.Content = $"[{shortcut}]\n{Path.GetFileNameWithoutExtension(path)}";
                    pad.Foreground = Brushes.White;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ses yüklenirken hata oluştu: {ex.Message}");
            }
        }

        private void TriggerPad(int index)
        {
            Button pad = (Button)FindName($"Pad{index + 1}");

            // Görsel efekt: Neon turuncu patlama animasyonu
            if (pad != null)
            {
                AnimatePadPress(pad);
            }

            // Sesi miksere ilet ve anında çal
            if (_cachedSounds[index] != null)
            {
                _mixer.AddMixerInput(new CachedSoundSampleProvider(_cachedSounds[index]));
            }
        }

        private void AnimatePadPress(Button button)
        {
            var colorAnim = new ColorAnimation
            {
                From = Color.FromRgb(255, 69, 0), // Akai Neon Turuncu
                To = Color.FromRgb(45, 45, 48),    // Orijinal Koyu Gri
                Duration = TimeSpan.FromMilliseconds(200)
            };

            var brush = new SolidColorBrush(Color.FromRgb(255, 69, 0));
            button.Background = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        }

        protected override void OnClosed(EventArgs e)
        {
            _waveOut?.Dispose();
            base.OnClosed(e);
        }

        // Volume slider hareketi
        private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_volumeProvider != null)
            {
                _volumeProvider.Volume = (float)e.NewValue;
            }
        }

        // Hazırlanan kiti JSON olarak kaydetme
        private void SaveKit_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "MPC Kit Dosyası (*.mpckit)|*.mpckit",
                DefaultExt = "mpckit",
                FileName = "BenimKitim.mpckit"
            };

            if (saveDialog.ShowDialog() == true)
            {
                string json = JsonSerializer.Serialize(_audioFilePaths);
                File.WriteAllText(saveDialog.FileName, json);
                MessageBox.Show("Kit başarıyla kaydedildi!");
            }
        }

        // Kayıtlı kiti yükleme
        private void LoadKit_Click(object sender, RoutedEventArgs e)
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "MPC Kit Dosyası (*.mpckit)|*.mpckit"
            };

            if (openDialog.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(openDialog.FileName);
                    string[] paths = JsonSerializer.Deserialize<string[]>(json);

                    if (paths != null)
                    {
                        for (int i = 0; i < paths.Length && i < 16; i++)
                        {
                            if (!string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i]))
                            {
                                LoadSampleToPad(i, paths[i]);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Kit yüklenirken hata oluştu: {ex.Message}");
                }
            }
        }
    }

    // Sıfır gecikme için sesi RAM'e alan ve formatı (stereo 44.1) eşitleyen sınıflar
    public class CachedSound
    {
        public float[] AudioData { get; }
        public WaveFormat WaveFormat { get; }

        public CachedSound(string audioFileName)
        {
            using (var audioFileReader = new AudioFileReader(audioFileName))
            {
                // Standart 44.1kHz stereo formata dönüştür
                var resampler = new WdlResamplingSampleProvider(audioFileReader, 44100);
                var sampleChannel = resampler.ToStereo();

                WaveFormat = sampleChannel.WaveFormat;
                var wholeFile = new List<float>();
                var buffer = new float[sampleChannel.WaveFormat.SampleRate * sampleChannel.WaveFormat.Channels];
                int samplesRead;
                while ((samplesRead = sampleChannel.Read(buffer, 0, buffer.Length)) > 0)
                {
                    wholeFile.AddRange(buffer.Take(samplesRead));
                }
                AudioData = wholeFile.ToArray();
            }
        }
    }

    public class CachedSoundSampleProvider : ISampleProvider
    {
        private readonly CachedSound _cachedSound;
        private long _position;

        public CachedSoundSampleProvider(CachedSound cachedSound)
        {
            _cachedSound = cachedSound;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var availableSamples = _cachedSound.AudioData.Length - _position;
            var samplesToCopy = Math.Min(availableSamples, count);
            Array.Copy(_cachedSound.AudioData, _position, buffer, offset, samplesToCopy);
            _position += samplesToCopy;
            return (int)samplesToCopy;
        }

        public WaveFormat WaveFormat => _cachedSound.WaveFormat;


    }


}