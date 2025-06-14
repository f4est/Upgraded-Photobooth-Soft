using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using System.Threading.Tasks;
using System.Drawing;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Windows.Media;
using System.Diagnostics;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace UnifiedPhotoBooth
{
    public partial class PhotoBoothPage : Page
    {
        private GoogleDriveService _driveService;
        private string _eventFolderId;
        private string _eventName;
        
        private VideoCapture _capture;
        private DispatcherTimer _previewTimer;
        private bool _previewRunning;
        
        private int _currentPhotoIndex = 0;
        private List<Mat> _capturedPhotos;
        private string _finalImagePath;
        private string _lastUniversalFolderId;
        
        private DispatcherTimer _countdownTimer;
        private int _countdownValue;
        private Action _onCountdownComplete;
        
        public PhotoBoothPage(GoogleDriveService driveService, string eventFolderId = null)
        {
            InitializeComponent();
            
            _driveService = driveService;
            _eventFolderId = eventFolderId;
            _capturedPhotos = new List<Mat>();
            
            if (!string.IsNullOrEmpty(_eventFolderId))
            {
                _eventName = GetEventNameById(_eventFolderId);
                txtEventName.Text = _eventName;
            }
            
            // Инициализация таймеров
            _previewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 fps
            };
            _previewTimer.Tick += PreviewTimer_Tick;
            
            _countdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _countdownTimer.Tick += CountdownTimer_Tick;
            
            Loaded += PhotoBoothPage_Loaded;
            Unloaded += PhotoBoothPage_Unloaded;
        }
        
        private string GetEventNameById(string eventId)
        {
            try
            {
                var events = _driveService.ListEvents();
                foreach (var evt in events)
                {
                    if (evt.Value == eventId)
                    {
                        return evt.Key;
                    }
                }
            }
            catch { }
            
            return "Неизвестное событие";
        }
        
        private void PhotoBoothPage_Loaded(object sender, RoutedEventArgs e)
        {
            StartPreview();
        }
        
        private void PhotoBoothPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopPreview();
            
            // Освобождаем ресурсы
            if (_capturedPhotos != null)
            {
                foreach (var photo in _capturedPhotos)
                {
                    photo?.Dispose();
                }
                _capturedPhotos.Clear();
            }
        }
        
        private void StartPreview()
        {
            try
            {
                // Инициализация камеры
                _capture = new VideoCapture(SettingsWindow.AppSettings.CameraIndex);
                if (!_capture.IsOpened())
                {
                    ShowError("Не удалось открыть камеру. Проверьте настройки.");
                    return;
                }
                
                // Устанавливаем максимально возможное разрешение камеры
                _capture.Set(VideoCaptureProperties.FrameWidth, 1920);
                _capture.Set(VideoCaptureProperties.FrameHeight, 1080);
                
                _previewRunning = true;
                _previewTimer.Start();
                
                ShowStatus("Готов к съемке", "Нажмите 'Начать', чтобы сделать фотографии");
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка при запуске предпросмотра: {ex.Message}");
            }
        }
        
        private void StopPreview()
        {
            _previewRunning = false;
            _previewTimer.Stop();
            
            _capture?.Dispose();
            _capture = null;
        }
        
        private void PreviewTimer_Tick(object sender, EventArgs e)
        {
            if (!_previewRunning || _capture == null || !_capture.IsOpened())
                return;
            
            using (var frame = new Mat())
            {
                if (_capture.Read(frame))
                {
                    // Применяем поворот, если необходимо
                    ApplyRotation(frame);
                    
                    // Применяем зеркальное отображение, если включено
                    if (SettingsWindow.AppSettings.MirrorMode)
                    {
                        Cv2.Flip(frame, frame, FlipMode.Y);
                    }
                    
                    // Обрабатываем фото в соответствии с настройками
                    Mat processedFrame = AdjustAspectRatioByMode(frame, 1200.0 / 1800.0, SettingsWindow.AppSettings.PhotoProcessingMode);
                    
                    // Отображаем кадр
                    imgPreview.Source = BitmapSourceConverter.ToBitmapSource(processedFrame);
                    
                    // Освобождаем ресурсы
                    if (processedFrame != frame)
                    {
                        processedFrame.Dispose();
                    }
                }
            }
        }
        
        private Mat AdjustAspectRatioByMode(Mat frame, double targetAspectRatio, ImageProcessingMode mode)
        {
            if (frame == null)
                return null;

            // Целевые размеры
            int targetWidth = 1800;
            int targetHeight = 1200;
            double targetRatio = (double)targetWidth / targetHeight; // 1.5 для 6:4

            // Получаем текущие размеры
            int currentWidth = frame.Width;
            int currentHeight = frame.Height;
            double currentRatio = (double)currentWidth / currentHeight;

            // Создаем новое изображение для результата
            Mat result;

            // Определяем, как нужно обрезать изображение
            if (currentRatio > targetRatio) 
            {
                // Изображение шире, чем нужно - обрезаем по бокам
                int newWidth = (int)(currentHeight * targetRatio);
                int x = (currentWidth - newWidth) / 2;
                
                // Вырезаем центральную часть с нужным соотношением сторон
                var roi = new Rect(x, 0, newWidth, currentHeight);
                var cropped = new Mat(frame, roi);
                
                // Изменяем размер до целевого
                result = new Mat();
                Cv2.Resize(cropped, result, new OpenCvSharp.Size(targetWidth, targetHeight));
                
                cropped.Dispose();
            }
            else if (currentRatio < targetRatio)
            {
                // Изображение выше, чем нужно - обрезаем сверху и снизу
                int newHeight = (int)(currentWidth / targetRatio);
                int y = (currentHeight - newHeight) / 2;
                
                // Вырезаем центральную часть с нужным соотношением сторон
                var roi = new Rect(0, y, currentWidth, newHeight);
                var cropped = new Mat(frame, roi);
                
                // Изменяем размер до целевого
                result = new Mat();
                Cv2.Resize(cropped, result, new OpenCvSharp.Size(targetWidth, targetHeight));
                
                cropped.Dispose();
            }
            else
            {
                // Соотношение сторон уже правильное, просто изменяем размер
                result = new Mat();
                Cv2.Resize(frame, result, new OpenCvSharp.Size(targetWidth, targetHeight));
            }

            return result;
        }
        
        private void ApplyRotation(Mat frame)
        {
            OpenCvSharp.RotateFlags? rotateMode = null;
            
            switch (SettingsWindow.AppSettings.RotationMode)
            {
                case "90° вправо (вертикально)":
                    rotateMode = OpenCvSharp.RotateFlags.Rotate90Clockwise;
                    break;
                case "90° влево (вертикально)":
                    rotateMode = OpenCvSharp.RotateFlags.Rotate90Counterclockwise;
                    break;
                case "180°":
                    rotateMode = OpenCvSharp.RotateFlags.Rotate180;
                    break;
            }
            
            if (rotateMode.HasValue)
            {
                Cv2.Rotate(frame, frame, rotateMode.Value);
            }
        }
        
        private void StartCountdown(int seconds, Action onComplete)
        {
            _countdownValue = seconds;
            txtCountdown.Text = _countdownValue.ToString();
            txtCountdown.Visibility = Visibility.Visible;
            
            // Устанавливаем делегат для обратного вызова по завершении отсчета
            _onCountdownComplete = onComplete;
            
            // Запускаем таймер обратного отсчета
            _countdownTimer.Start();
        }
        
        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            // Убираем проверку на выбор события
            // Очищаем предыдущие фотографии
            foreach (var photo in _capturedPhotos)
            {
                photo?.Dispose();
            }
            _capturedPhotos.Clear();
            
            // Сбрасываем индекс текущей фотографии
            _currentPhotoIndex = 0;
            
            // Скрываем результат, если он был показан
            imgResult.Visibility = Visibility.Collapsed;
            imgQrCode.Visibility = Visibility.Collapsed;
            txtQrCodeUrl.Visibility = Visibility.Collapsed;
            
            // Показываем превью
            imgPreview.Visibility = Visibility.Visible;
            
            // Обновляем интерфейс
            btnStart.Visibility = Visibility.Collapsed;
            btnReset.Visibility = Visibility.Visible;
            btnShare.Visibility = Visibility.Collapsed;
            btnPrint.Visibility = Visibility.Collapsed;
            
            ShowStatus("Подготовка к съемке", "Позируйте для фотографии");
            
            // Запускаем процесс съемки
            StartPhotoProcess();
        }
        
        private void StartPhotoProcess()
        {
            // Запускаем отсчет
            StartCountdown(SettingsWindow.AppSettings.PhotoCountdownTime, CapturePhoto);
        }
        
        private void CapturePhoto()
        {
            if (!_previewRunning || _capture == null)
                return;
            
            using (var frame = new Mat())
            {
                if (_capture.Read(frame))
                {
                    // Применяем поворот и зеркальное отображение
                    ApplyRotation(frame);
                    
                    if (SettingsWindow.AppSettings.MirrorMode)
                    {
                        Cv2.Flip(frame, frame, FlipMode.Y);
                    }
                    
                    // Обрабатываем фото в соответствии с настройками
                    Mat processedFrame = AdjustAspectRatioByMode(frame, 1200.0 / 1800.0, SettingsWindow.AppSettings.PhotoProcessingMode);
                    
                    // Добавляем обработанное фото в список
                    _capturedPhotos.Add(processedFrame);
                    
                    ShowStatus($"Фото {_currentPhotoIndex + 1} из {SettingsWindow.AppSettings.PhotoCount}", "Фотография сделана!");
                    
                    _currentPhotoIndex++;
                    
                    // Проверяем, нужно ли сделать еще фотографии
                    if (_currentPhotoIndex < SettingsWindow.AppSettings.PhotoCount)
                    {
                        // Даем паузу между фотографиями
                        Task.Delay(1000).ContinueWith(t => 
                        {
                            Dispatcher.Invoke(() => 
                            {
                                ShowStatus($"Подготовка к фото {_currentPhotoIndex + 1}", "Позируйте для следующей фотографии");
                                StartPhotoProcess();
                            });
                        });
                    }
                    else
                    {
                        // Все фотографии сделаны, переходим к их обработке
                        Task.Delay(1000).ContinueWith(t =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                ShowStatus("Обработка фотографий", "Пожалуйста, подождите...");
                                ProcessPhotos();
                            });
                        });
                    }
                }
                else
                {
                    ShowError("Не удалось сделать фотографию. Проверьте камеру.");
                }
            }
        }
        
        private Mat ProcessPhoto(Mat photo, double targetWidth, double targetHeight)
        {
            if (photo == null)
                return null;
            
            // Просто изменяем размер фото без дополнительной обработки
            Mat processedPhoto = new Mat();
            Cv2.Resize(photo, processedPhoto, new OpenCvSharp.Size(targetWidth, targetHeight));
            return processedPhoto;
        }
        
        private Mat AdjustAspectRatio(Mat frame, double targetAspectRatio)
        {
            // Используем обрезку как режим по умолчанию для обратной совместимости
            return AdjustAspectRatioByMode(frame, targetAspectRatio, ImageProcessingMode.Crop);
        }
        
        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            // Сбрасываем состояние
            _currentPhotoIndex = 0;
            
            foreach (var photo in _capturedPhotos)
            {
                photo?.Dispose();
            }
            _capturedPhotos.Clear();
            
            // Обновляем интерфейс
            imgResult.Visibility = Visibility.Collapsed;
            imgQrCode.Visibility = Visibility.Collapsed;
            txtQrCodeUrl.Visibility = Visibility.Collapsed;
            imgPreview.Visibility = Visibility.Visible;
            
            btnStart.Visibility = Visibility.Visible;
            btnReset.Visibility = Visibility.Collapsed;
            btnShare.Visibility = Visibility.Collapsed;
            btnPrint.Visibility = Visibility.Collapsed;
            
            ShowStatus("Готов к съемке", "Нажмите 'Начать', чтобы сделать фотографии");
        }
        
        private async void BtnShare_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_finalImagePath) || !File.Exists(_finalImagePath))
            {
                ShowError("Файл коллажа не найден.");
                return;
            }
            
            try
            {
                ShowStatus("Загрузка фото", "Пожалуйста, подождите...");
                
                // Отключаем кнопки на время загрузки
                btnReset.IsEnabled = false;
                btnShare.IsEnabled = false;
                btnPrint.IsEnabled = false;
                
                // Загружаем фото в Google Drive
                string folderName = $"PhotoBooth_{System.DateTime.Now:yyyyMMdd_HHmmss}";
                var result = await _driveService.UploadPhotoAsync(_finalImagePath, folderName, _eventFolderId, false, _lastUniversalFolderId);
                
                // Сохраняем ID папки для возможного повторного использования
                _lastUniversalFolderId = result.FolderId;
                
                // Конвертируем QR-код в формат для отображения
                BitmapImage qrImage = new BitmapImage();
                using (MemoryStream ms = new MemoryStream())
                {
                    result.QrCode.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    qrImage.BeginInit();
                    qrImage.CacheOption = BitmapCacheOption.OnLoad;
                    qrImage.StreamSource = ms;
                    qrImage.EndInit();
                }
                
                // Отображаем только QR-код без ссылки
                imgQrCode.Source = qrImage;
                imgQrCode.Visibility = Visibility.Visible;
                
                // Скрываем текстовое поле ссылки
                txtQrCodeUrl.Visibility = Visibility.Collapsed;
                
                ShowStatus("Фото загружено", "Отсканируйте QR-код для доступа к фотографии");
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка при загрузке фото: {ex.Message}");
            }
            finally
            {
                // Включаем кнопки
                btnReset.IsEnabled = true;
                btnShare.IsEnabled = true;
                btnPrint.IsEnabled = true;
            }
        }
        
        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_finalImagePath == null || !File.Exists(_finalImagePath))
                {
                    MessageBox.Show("Нет изображения для печати.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Создаем окно печати с расширенными настройками
                System.Windows.Controls.PrintDialog printDialog = new System.Windows.Controls.PrintDialog();
                
                // Настраиваем параметры печати
                printDialog.PageRangeSelection = PageRangeSelection.AllPages;
                printDialog.UserPageRangeEnabled = true;
                
                // Определяем, нужно ли изображение подогнать под размер кадра
                bool fitToFrame = MessageBox.Show("Растянуть изображение по размеру кадра? (Да - растянуть, Нет - сохранить пропорции)", 
                    "Настройка печати", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                
                // Создаем изображение для печати
                BitmapImage bitmapImage = new BitmapImage(new Uri(_finalImagePath, UriKind.Absolute));
                System.Windows.Controls.Image printImage = new System.Windows.Controls.Image();
                printImage.Source = bitmapImage;
                
                // Настраиваем параметры печати в зависимости от выбора пользователя
                if (fitToFrame)
                {
                    // Растягиваем изображение на всю страницу
                    printImage.Stretch = Stretch.Fill;
                }
                else
                {
                    // Сохраняем пропорции
                    printImage.Stretch = Stretch.Uniform;
                }
                
                // Настраиваем размер печати для формата 4x6
                printDialog.PrintTicket.PageMediaSize = new System.Printing.PageMediaSize(
                    4 * 96.0, // 4 дюйма в пикселях
                    6 * 96.0  // 6 дюймов в пикселях
                );
                
                // Показываем диалог печати
                if (printDialog.ShowDialog() == true)
                {
                    // Печатаем изображение
                    printDialog.PrintVisual(printImage, "Печать фотографии");
                    ShowStatus("Печать", "Задание отправлено на печать");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при печати: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void PrintImage(System.Windows.Controls.PrintDialog printDialog, string imagePath)
        {
            try
            {
                // Загружаем изображение
                BitmapImage bitmap = new BitmapImage(new Uri(imagePath));
                
                // Определяем ориентацию изображения
                bool isImageVertical = bitmap.PixelHeight > bitmap.PixelWidth;
                
                // Получаем размеры страницы принтера
                double printWidth = SettingsWindow.AppSettings.PrintWidth;
                double printHeight = SettingsWindow.AppSettings.PrintHeight;
                
                // Создаем контейнер для изображения с учетом ориентации
                System.Windows.Controls.Canvas canvas = new System.Windows.Controls.Canvas();
                
                // Задаем размеры для холста в зависимости от ориентации изображения
                if (isImageVertical)
                {
                    // Если изображение вертикальное, используем вертикальную ориентацию бумаги
                    canvas.Width = ConvertCmToPixels(Math.Min(printWidth, printHeight));
                    canvas.Height = ConvertCmToPixels(Math.Max(printWidth, printHeight));
                }
                else
                {
                    // Если изображение горизонтальное, используем горизонтальную ориентацию бумаги
                    canvas.Width = ConvertCmToPixels(Math.Max(printWidth, printHeight));
                    canvas.Height = ConvertCmToPixels(Math.Min(printWidth, printHeight));
                }
                
                canvas.Background = System.Windows.Media.Brushes.White;
                
                // Создаем изображение для печати
                System.Windows.Controls.Image printImage = new System.Windows.Controls.Image();
                printImage.Source = bitmap;
                printImage.Width = canvas.Width;
                printImage.Height = canvas.Height;
                printImage.Stretch = Stretch.Uniform;
                
                // Добавляем изображение на холст
                canvas.Children.Add(printImage);
                
                // Печать
                printDialog.PrintVisual(canvas, "PhotoboothPro - Печать");
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка при подготовке изображения для печати: {ex.Message}");
            }
        }
        
        private double ConvertCmToPixels(double cm)
        {
            // Разрешение экрана приблизительно 96 DPI
            const double INCH_TO_CM = 2.54;
            const double DPI = 96;
            return cm * DPI / INCH_TO_CM;
        }
        
        private void ShowStatus(string status, string info)
        {
            txtStatus.Text = status;
            txtInfo.Text = info;
        }
        
        private void ShowError(string message)
        {
            ShowStatus("Ошибка", message);
            MessageBox.Show(message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        
        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            _countdownValue--;
            
            if (_countdownValue > 0)
            {
                txtCountdown.Text = _countdownValue.ToString();
            }
            else
            {
                _countdownTimer.Stop();
                txtCountdown.Visibility = Visibility.Collapsed;
                
                // Делаем снимок после завершения отсчета
                _onCountdownComplete?.Invoke();
            }
        }
        
        private void UpdateShareButtonVisibility()
        {
            // Показываем кнопку "Поделиться" только если есть интернет
            btnShare.Visibility = _driveService.IsOnline ? Visibility.Visible : Visibility.Collapsed;
        }
        
        private Mat CreateCollage()
        {
            try
            {
                // Создаем коллаж на основе шаблона, если он есть
                string frameTemplatePath = SettingsWindow.AppSettings.FrameTemplatePath;
                Mat result;
                
                // Всегда используем фиксированные размеры: 1200x1800, независимо от ориентации
                int finalWidth = 1200;
                int finalHeight = 1800;
                
                if (!string.IsNullOrEmpty(frameTemplatePath) && File.Exists(frameTemplatePath))
                {
                    // Загружаем шаблон
                    Mat frameTemplate = Cv2.ImRead(frameTemplatePath);
                    
                    // Масштабируем шаблон до нужного размера, если необходимо
                    if (frameTemplate.Width != finalWidth || frameTemplate.Height != finalHeight)
                    {
                        Cv2.Resize(frameTemplate, frameTemplate, new OpenCvSharp.Size(finalWidth, finalHeight));
                    }
                    
                    // Стандартный подход - фото поверх рамки
                    result = frameTemplate;
                    
                    // Вставляем фотографии в позиции на шаблоне
                    var positions = SettingsWindow.AppSettings.PhotoPositions;
                    
                    // Логируем информацию о позициях и фотографиях
                    System.Diagnostics.Debug.WriteLine($"Создание коллажа: найдено {positions.Count} позиций и {_capturedPhotos.Count} фотографий");
                    for (int i = 0; i < positions.Count; i++)
                    {
                        var pos = positions[i];
                        System.Diagnostics.Debug.WriteLine($"Позиция {i+1}: X={pos.X}, Y={pos.Y}, Width={pos.Width}, Height={pos.Height}");
                    }
                    
                    // Если позиций меньше, чем фотографий, используем только доступные позиции
                    int photoCount = Math.Min(_capturedPhotos.Count, positions.Count);
                    System.Diagnostics.Debug.WriteLine($"Будет размещено {photoCount} фотографий");
                    
                    for (int i = 0; i < photoCount; i++)
                    {
                        // Дополнительная проверка на выход за границы массива
                        if (i >= _capturedPhotos.Count || i >= positions.Count) break;
                        
                        var photo = _capturedPhotos[i].Clone(); // Клонируем для безопасности операций
                        var pos = positions[i];
                        
                        // Изменяем размер фото для соответствия позиции в шаблоне
                        Mat processedPhoto = new Mat();
                        Cv2.Resize(photo, processedPhoto, new OpenCvSharp.Size(pos.Width, pos.Height));
                        photo.Dispose();
                        
                        // Проверяем границы перед созданием ROI
                        int x = (int)Math.Max(0, Math.Min(pos.X, result.Width - 1));
                        int y = (int)Math.Max(0, Math.Min(pos.Y, result.Height - 1));
                        int w = (int)Math.Min(pos.Width, result.Width - x);
                        int h = (int)Math.Min(pos.Height, result.Height - y);
                        
                        // Проверяем, что размеры не равны нулю
                        if (w <= 0 || h <= 0) continue;
                        
                        // Создаем ROI (Region of Interest) для вставки
                        try
                        {
                            // Проверяем, что размеры фото соответствуют размерам ROI
                            if (processedPhoto.Width != w || processedPhoto.Height != h)
                            {
                                var temp = new Mat();
                                Cv2.Resize(processedPhoto, temp, new OpenCvSharp.Size(w, h));
                                processedPhoto.Dispose();
                                processedPhoto = temp;
                            }
                            
                            var roi = new Mat(result, new OpenCvSharp.Rect(x, y, w, h));
                            processedPhoto.CopyTo(roi);
                            processedPhoto.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                ShowError($"Ошибка при вставке фото {i+1}: {ex.Message}");
                            });
                        }
                    }
                }
                else
                {
                    // Если шаблона нет, создаем простой коллаж из фотографий
                    // Создаем пустое изображение для коллажа с соблюдением пропорций
                    result = new Mat(finalHeight, finalWidth, MatType.CV_8UC3, Scalar.Black);
                    
                    // Определяем размер и расположение областей в зависимости от количества фотографий
                    int cols, rows;
                    int photoWidth, photoHeight;
                    int margin = 10; // Отступ между фотографиями в пикселях
                    
                    if (_capturedPhotos.Count == 1) {
                        cols = 1;
                        rows = 1;
                        photoWidth = finalWidth - 2 * margin;
                        photoHeight = finalHeight - 2 * margin;
                    }
                    else if (_capturedPhotos.Count == 2) {
                        cols = 1;
                        rows = 2;
                        photoWidth = finalWidth - 2 * margin;
                        photoHeight = (finalHeight - 3 * margin) / 2;
                    }
                    else if (_capturedPhotos.Count == 3) {
                        // Для 3 фотографий: 2 сверху, 1 по центру снизу
                        cols = 2;
                        rows = 2;
                        photoWidth = (finalWidth - 3 * margin) / 2;
                        photoHeight = (finalHeight - 3 * margin) / 2;
                    }
                    else { // 4 и более фотографий
                        cols = 2;
                        rows = 2;
                        photoWidth = (finalWidth - 3 * margin) / 2;
                        photoHeight = (finalHeight - 3 * margin) / 2;
                    }
                    
                    // Добавляем фотографии в коллаж
                    int maxPhotos = Math.Min(_capturedPhotos.Count, cols * rows);
                    for (int i = 0; i < maxPhotos; i++) {
                        // Дополнительная проверка на выход за границы массива
                        if (i >= _capturedPhotos.Count) break;
                        
                        int row, col;
                        
                        if (_capturedPhotos.Count == 3 && i == 2) {
                            // Для третьей фотографии при трех фото - помещаем по центру внизу
                            row = 1;
                            col = 0;
                            // Для центрирования используем более широкое фото
                            var centerPhotoWidth = finalWidth - 2 * margin;
                            
                            var photo = _capturedPhotos[i].Clone();
                            
                            // Масштабируем фото до нужного размера
                            Mat processedPhoto = new Mat();
                            Cv2.Resize(photo, processedPhoto, new OpenCvSharp.Size(centerPhotoWidth, photoHeight));
                            photo.Dispose();
                            
                            var centerRoi = new Mat(result, new OpenCvSharp.Rect(
                                margin, // центрируем по горизонтали
                                (rows - 1) * photoHeight + rows * margin,
                                centerPhotoWidth, 
                                photoHeight));
                            
                            processedPhoto.CopyTo(centerRoi);
                            processedPhoto.Dispose();
                            continue;
                        }
                        else {
                            // Для обычного расположения
                            row = i / cols;
                            col = i % cols;
                        }
                        
                        var regularPhoto = _capturedPhotos[i].Clone();
                        
                        // Масштабируем фото до нужного размера
                        Mat processedRegularPhoto = new Mat();
                        Cv2.Resize(regularPhoto, processedRegularPhoto, new OpenCvSharp.Size(photoWidth, photoHeight));
                        regularPhoto.Dispose();
                        
                        var resultRoi = new Mat(result, new OpenCvSharp.Rect(
                            col * photoWidth + (col + 1) * margin, 
                            row * photoHeight + (row + 1) * margin, 
                            photoWidth, 
                            photoHeight));
                        
                        processedRegularPhoto.CopyTo(resultRoi);
                        processedRegularPhoto.Dispose();
                    }
                }
                
                // Добавляем текстовые элементы из настроек
                var textElements = SettingsWindow.AppSettings.TextElements;
                if (textElements != null && textElements.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Добавление {textElements.Count} текстовых элементов на коллаж");
                    
                    foreach (var textElem in textElements)
                    {
                        // Пропускаем отключенные элементы
                        if (!textElem.Enabled) continue;
                        
                        // Формируем текст для отображения
                        string textToDisplay = "";
                        
                        switch (textElem.Type)
                        {
                            case TextElementType.DateTime:
                                textToDisplay = System.DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                                break;
                                
                            case TextElementType.EventName:
                                if (!string.IsNullOrEmpty(_eventName))
                                {
                                    textToDisplay = _eventName;
                                }
                                break;
                                
                            case TextElementType.Custom:
                                textToDisplay = textElem.Text;
                                break;
                        }
                        
                        // Добавляем текст только если он не пустой
                        if (!string.IsNullOrEmpty(textToDisplay))
                        {
                            // Парсим цвет текста (по умолчанию черный)
                            Scalar textColor = Scalar.Black;
                            if (!string.IsNullOrEmpty(textElem.FontColor) && textElem.FontColor.StartsWith("#"))
                            {
                                try
                                {
                                    var color = System.Windows.Media.ColorConverter.ConvertFromString(textElem.FontColor) as System.Windows.Media.Color?;
                                    if (color.HasValue)
                                    {
                                        textColor = new Scalar(color.Value.B, color.Value.G, color.Value.R); // BGR в OpenCV
                                    }
                                }
                                catch
                                {
                                    // В случае ошибки используем черный цвет
                                }
                            }
                            
                            // Отрисовываем текст на коллаже с использованием настраиваемых шрифтов
                            DrawTextWithCustomFont(
                                result,
                                textToDisplay,
                                textElem,
                                textColor
                            );
                        }
                    }
                }
                else
                {
                    // Для обратной совместимости добавляем стандартные надписи, если нет настроенных элементов
                    System.Diagnostics.Debug.WriteLine("Нет настроенных текстовых элементов, добавляем стандартные надписи");
                    
                    // Добавляем дату и название события
                    string dateTime = System.DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                    Cv2.PutText(result, dateTime, new OpenCvSharp.Point(20, result.Height - 20), 
                                HersheyFonts.HersheyComplexSmall, 1, Scalar.Black, 2);
                    
                    if (!string.IsNullOrEmpty(_eventName))
                    {
                        Cv2.PutText(result, _eventName, new OpenCvSharp.Point(20, 40), 
                                    HersheyFonts.HersheyComplexSmall, 1, Scalar.Black, 2);
                    }
                }
                
                // Сохраняем результат
                string fileName = $"PhotoBooth_{System.DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                _finalImagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "photos", fileName);
                
                Directory.CreateDirectory(Path.GetDirectoryName(_finalImagePath));
                Cv2.ImWrite(_finalImagePath, result);
                
                return result;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ShowError($"Ошибка при создании коллажа: {ex.Message}");
                });
                
                // В случае ошибки возвращаем пустое изображение
                // Всегда используем фиксированные размеры итогового изображения
                int width = 1200;
                int height = 1800;
                return new Mat(height, width, MatType.CV_8UC3, Scalar.White);
            }
        }
        
        /// <summary>
        /// Отрисовывает текст на изображении с учетом настроек шрифта
        /// </summary>
        private void DrawTextWithCustomFont(Mat image, string text, TextElement textElem, Scalar color)
        {
            try
            {
                // Создаем временное изображение с прозрачностью для наложения текста
                using (var bitmap = new System.Drawing.Bitmap(image.Width, image.Height))
                {
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        // Настраиваем качество отрисовки
                        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        
                        // Определяем стиль шрифта
                        System.Drawing.FontStyle fontStyle = System.Drawing.FontStyle.Regular;
                        if (textElem.IsBold) fontStyle |= System.Drawing.FontStyle.Bold;
                        if (textElem.IsItalic) fontStyle |= System.Drawing.FontStyle.Italic;
                        if (textElem.IsUnderline) fontStyle |= System.Drawing.FontStyle.Underline;
                        
                        // Создаем шрифт с указанными параметрами
                        using (var font = new System.Drawing.Font(
                            textElem.FontFamily, 
                            (float)(14 * textElem.FontSize), // Базовый размер 14px умножаем на масштаб
                            fontStyle, 
                            System.Drawing.GraphicsUnit.Pixel))
                        {
                            // Преобразуем Scalar (BGR) в Color (RGB)
                            var drawColor = System.Drawing.Color.FromArgb(
                                255, // Альфа-канал (непрозрачность)
                                (int)color[2], // Красный (R из BGR)
                                (int)color[1], // Зеленый (G из BGR)
                                (int)color[0]  // Синий (B из BGR)
                            );
                            
                            // Создаем кисть для текста
                            using (var brush = new System.Drawing.SolidBrush(drawColor))
                            {
                                // Отрисовываем текст на точных координатах
                                graphics.DrawString(text, font, brush, (float)textElem.X, (float)textElem.Y);
                            }
                        }
                    }
                    
                    // Конвертируем Bitmap обратно в Mat и накладываем на оригинальное изображение
                    using (var ms = new MemoryStream())
                    {
                        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        byte[] imageData = ms.ToArray();
                        
                        using (var textOverlay = Mat.FromImageData(imageData, ImreadModes.Unchanged))
                        {
                            // Учитываем возможное различие в каналах
                            if (textOverlay.Channels() == 4 && image.Channels() == 3)
                            {
                                // Обрабатываем прозрачность и накладываем текст
                                for (int y = 0; y < textOverlay.Height; y++)
                                {
                                    for (int x = 0; x < textOverlay.Width; x++)
                                    {
                                        var pixel = textOverlay.At<Vec4b>(y, x);
                                        if (pixel[3] > 0) // Если пиксель не полностью прозрачный
                                        {
                                            // Установить пиксель в исходном изображении
                                            var alpha = pixel[3] / 255.0;
                                            if (alpha > 0.5) // Пороговое значение для видимого текста
                                            {
                                                image.Set(y, x, new Vec3b(pixel[0], pixel[1], pixel[2]));
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Прямое наложение, если формат каналов совпадает
                                var roi = new Mat(image, new OpenCvSharp.Rect(0, 0, textOverlay.Width, textOverlay.Height));
                                textOverlay.CopyTo(roi);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка при отрисовке текста: {ex.Message}");
                
                // Fallback - использовать стандартный метод
                Cv2.PutText(
                    image,
                    text,
                    new OpenCvSharp.Point(textElem.X, textElem.Y),
                    HersheyFonts.HersheySimplex,
                    textElem.FontSize,
                    color,
                    thickness: textElem.IsBold ? 2 : 1,
                    lineType: LineTypes.AntiAlias
                );
            }
        }
        
        private void ProcessPhotos()
        {
            // Создаем коллаж из обработанных фотографий
            var finalImage = CreateCollage();
            
            // Показываем результат
            imgPreview.Visibility = Visibility.Collapsed;
            imgResult.Source = BitmapSourceConverter.ToBitmapSource(finalImage);
            imgResult.Visibility = Visibility.Visible;
            
            ShowStatus("Коллаж готов", "Ваши фотографии готовы!");
            
            // Обновляем кнопки
            btnReset.Visibility = Visibility.Visible;
            UpdateShareButtonVisibility();
            btnPrint.Visibility = Visibility.Visible;
        }
    }
} 