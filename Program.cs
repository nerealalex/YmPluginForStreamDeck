using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Media.Control;
using Windows.Storage.Streams;

using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

internal static class Program
{
    private static readonly ClientWebSocket Socket = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly Dictionary<string, string> Buttons = new();
    private static readonly Dictionary<string, (string Title, string Image)> Sent = new();

    private static Manager? manager;
    private static string? mediaKey;
    private static string? cover;
    private static string lastTrackTitle = "";
    private static string lastTrackArtist = "";


    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YmDeck", "plugin.log");

    private static async Task Main(string[] args)
    {
        try
        {
            // Log("Старт YmDeck");
            string Arg(string name)
            {
                int i = Array.IndexOf(args, name);
                if (i < 0 || i + 1 >= args.Length)
                    throw new ArgumentException($"Нет параметра {name}");

                return args[i + 1];
            }

            string port = Arg("-port");
            if (!int.TryParse(port, out int portNumber) ||
                portNumber is < 1 or > 65535)
            {
                throw new ArgumentException("Параметр -port должен быть числом от 1 до 65535.");
            }

            string pluginId = Arg("-pluginUUID");
            string registerEvent = Arg("-registerEvent");

            // manager = await Manager.RequestAsync();
            // Log("Менеджер медиасессий получен");

            await Socket.ConnectAsync(
                new Uri($"ws://127.0.0.1:{portNumber}"),
                CancellationToken.None);

            await Send(new { @event = registerEvent, uuid = pluginId });
            // Log("Сообщение регистрации отправлено");

            using var stop = new CancellationTokenSource();
            Task polling = Poll(stop.Token);

            try
            {
                await Receive();
            }
            finally
            {
                stop.Cancel();
                await polling;
            }
        }
        catch (Exception e)
        {
            Log(e.ToString());
        }
    }

    private static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
        }
        catch
        {
            // Ошибка записи журнала не должна завершать плагин.
        }
    }

    private static async Task Send(object value)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        await Socket.SendAsync(
            new ArraySegment<byte>(data),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }

    private static async Task Receive()
    {
        byte[] buffer = new byte[8192];

        while (Socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult part;

            do
            {
                part = await Socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None);

                if (part.MessageType == WebSocketMessageType.Close)
                    return;

                message.Write(buffer, 0, part.Count);

                if (message.Length > 2_000_000)
                    throw new InvalidDataException("Слишком большое сообщение.");
            }
            while (!part.EndOfMessage);

            if (part.MessageType != WebSocketMessageType.Text)
                continue;

            using var json = JsonDocument.Parse(message.ToArray());
            JsonElement root = json.RootElement;

            string ev = root.GetProperty("event").GetString() ?? "";

            if (!root.TryGetProperty("context", out var contextElement))
                continue;

            string context = contextElement.GetString() ?? "";

            await Gate.WaitAsync();
            try
            {
                if (ev == "willAppear")
                {
                    // Log("Stream Deck сообщил о появлении кнопки");
                    string action = root.GetProperty("action").GetString() ?? "";
                    Buttons[context] = action.Split('.').Last();
                    Sent.Remove(context);

                    await Refresh();
                }
                else if (ev == "willDisappear")
                {
                    Buttons.Remove(context);
                    Sent.Remove(context);
                }
                else if (ev == "keyDown" &&
                         Buttons.TryGetValue(context, out string? action))
                {
                    // Log($"Получен keyDown: action={action}, context={context}");
                    try
                    {
                        await Execute(action);
                        // Log($"Обработчик кнопки завершён: action={action}");
                    }
                    catch (Exception e)
                    {
                        Log(e.ToString());
                        await Send(new { @event = "showAlert", context });
                    }

                    await Refresh();
                }
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }
            finally
            {
                Gate.Release();
            }
        }
    }

    private static async Task Poll(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                await Gate.WaitAsync(token);

                try
                {
                    if (Socket.State != WebSocketState.Open)
                        return;

                    await Refresh();
                }
                catch (Exception e)
                {
                    Log(e.ToString());
                }
                finally
                {
                    Gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsYandexMusic(string value)
    {
        string normalized = new(
            value.ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());

        return normalized.Contains("yandexmusic") ||
               normalized.Contains("яндексмузыка");
    }

    private static string? lastSessionList;

    private static DateTimeOffset nextManagerRequestAttempt = DateTimeOffset.MinValue;

    private static async Task<Manager> RequestManagerAsync()
    {
        // RequestAsync может зависнуть внутри Windows. Не сохраняем
        // незавершённый Task навсегда: после паузы пробуем новый запрос.
        if (DateTimeOffset.Now < nextManagerRequestAttempt)
        {
            throw new TimeoutException(
                "Запрос менеджера медиасессий недавно завершился тайм-аутом; повторная попытка отложена.");
        }

        try
        {
            return await Task.Run(async () => await Manager.RequestAsync())
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            nextManagerRequestAttempt = DateTimeOffset.Now.AddSeconds(15);
            throw new TimeoutException(
                "Windows не вернула менеджер медиасессий за 5 секунд. Повтор через 15 секунд.");
        }
    }

    private static async Task<Session?> FindSessionAsync()
    {
        List<Session> allSessions;

        try
        {
            manager = await RequestManagerAsync();
            allSessions = manager.GetSessions().ToList();
        }
        catch (Exception e)
        {
            // Во время периода ожидания не засоряем журнал одинаковыми тайм-аутами.
            if (e is not TimeoutException ||
                !e.Message.Contains("повторная попытка отложена", StringComparison.OrdinalIgnoreCase))
            {
                // Log("Ошибка получения списка медиасессий:\n" + e);
            }
            throw;
        }

        var entries = new List<(Session Session, string AppId)>();

        foreach (Session session in allSessions)
        {
            try
            {
                entries.Add((session, session.SourceAppUserModelId));
            }
            catch (Exception e)
            {
                // Log("Ошибка чтения имени медиасессии:\n" + e);
            }
        }

        string sessionList = JsonSerializer.Serialize(
            entries
                .Select(entry => entry.AppId)
                .OrderBy(id => id)
                .ToArray());

        if (sessionList != lastSessionList)
        {
            lastSessionList = sessionList;
            // Log("Медиасессии Windows: " + sessionList);
        }

        var matches = entries
            .Where(entry => string.Equals(
                entry.AppId,
                "ru.yandex.desktop.music",
                StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Session)
            .ToList();

        if (matches.Count == 0)
            return null;

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                "Найдено несколько медиасессий Яндекс Музыки.");
        }

        return matches[0];
    }

    private static async Task Execute(string action)
    {
        if (action == "track")
        {
            mediaKey = null;
            cover = null;
            return;
        }

        if (action == "like")
        {
            await SendRatingKey(0x21);
            return;
        }

        if (action == "dislike")
        {
            await SendRatingKey(0x20);
            return;
        }

        Session? session = null;
        try
        {
            session = await FindSessionAsync();
        }
        catch (Exception e)
        {
            // Если Windows Media Session API завис, используем глобальную
            // мультимедийную клавишу как резервный способ управления.
            // Log("Медиасессия недоступна; пробуем мультимедийную клавишу: " + e.Message);
        }

        if (session == null)
        {
            ushort mediaKeyCode = action switch
            {
                "previous" => 0xB1,  // VK_MEDIA_PREV_TRACK
                "playpause" => 0xB3, // VK_MEDIA_PLAY_PAUSE
                "next" => 0xB0,      // VK_MEDIA_NEXT_TRACK
                _ => (ushort)0
            };

            if (mediaKeyCode == 0)
                return;

            // Сначала пробуем WM_APPCOMMAND непосредственно для окна Яндекс Музыки.
            // SendInput с VK_MEDIA_* успешно принимается Windows, но приложение
            // может не обрабатывать эти глобальные клавиши.
            if (!TrySendYandexAppCommand(action))
                SendMediaKey(mediaKeyCode);
            return;
        }

        bool success;
        switch (action)
        {
            case "previous":
                success = await session.TrySkipPreviousAsync();
                break;
            case "playpause":
                success = await session.TryTogglePlayPauseAsync();
                break;
            case "next":
                success = await session.TrySkipNextAsync();
                break;
            default:
                return;
        }

        if (!success)
            throw new InvalidOperationException(
                "Приложение не выполнило медиакоманду.");
    }

    private static async Task Refresh()
    {
        if (Buttons.Count == 0)
            return;

        Session? session;
        string title = "";
        string artist = "";
        bool playing = false;
        string status = "Открой\nЯндекс Музыку";

        try
        {
            session = await FindSessionAsync();

            if (session != null)
            {
                var media = await session.TryGetMediaPropertiesAsync();

                title = media.Title ?? "";
                artist = media.Artist ?? "";

                if (!string.IsNullOrWhiteSpace(title))
                {
                    lastTrackTitle = title;
                    lastTrackArtist = artist;
                }

                playing = session.GetPlaybackInfo().PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                string key = JsonSerializer.Serialize(new[]
                {
                    session.SourceAppUserModelId,
                    title,
                    artist,
                    media.AlbumTitle ?? ""
                });

                if (key != mediaKey)
                {
                    mediaKey = key;
                    cover = null;
                }

                if (cover == null && media.Thumbnail != null)
                {
                    try
                    {
                        using var stream = await media.Thumbnail.OpenReadAsync();

                        if (stream.Size > 0 && stream.Size <= 8_000_000)
                        {
                            string mime = stream.ContentType.ToLowerInvariant();

                            if (mime is "image/png" or "image/jpeg")
                            {
                                using var reader = new DataReader(stream);
                                uint size = (uint)stream.Size;
                                uint loaded = await reader.LoadAsync(size);

                                if (loaded == size)
                                {
                                    byte[] bytes = new byte[(int)size];
                                    reader.ReadBytes(bytes);

                                    cover = $"data:{mime};base64," +
                                        Convert.ToBase64String(bytes);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        // Log("Обложка: " + e.Message);
                    }
                }

                status = string.IsNullOrWhiteSpace(title)
                    ? "Нет данных\nо треке"
                    : Short(title) + "\n" + Short(artist);
            }
            else
            {
                // Отсутствие сессии не обязательно означает,
                // что приложение закрыто.
                // Сохраняем последнюю обложку и явно помечаем данные.
                status = string.IsNullOrWhiteSpace(lastTrackTitle)
                    ? "Нет\nмедиасессии"
                    : "Последний трек:\n" +
                      Short(lastTrackTitle) + "\n" +
                      Short(lastTrackArtist);
            }
        }
        catch (Exception e)
        {
            // Повторный опрос выполняется каждую секунду, но не нужно писать
            // одну и ту же ошибку в журнал во время 15-секундной паузы.
            if (e is not TimeoutException ||
                !e.Message.Contains("повторная попытка отложена", StringComparison.OrdinalIgnoreCase))
            {
                // Log(e.ToString());
            }
            status = "Ошибка\nмедиасессии";
            cover = null;
        }

        foreach (var button in Buttons)
        {
            string action = button.Value;

            string buttonTitle = action switch
            {
                "track" => status,
                "playpause" => playing ? "Пауза" : "Играть",
                _ => ""
            };

            string image = action == "track" && cover != null
                ? cover
                : $"images/{action}.png";

            var visual = (Title: buttonTitle, Image: image);

            if (Sent.TryGetValue(button.Key, out var old) && old == visual)
                continue;

            await Send(new
            {
                @event = "setImage",
                context = button.Key,
                payload = new { image, target = 0 }
            });

            await Send(new
            {
                @event = "setTitle",
                context = button.Key,
                payload = new { title = buttonTitle, target = 0 }
            });

            Sent[button.Key] = visual;
        }
    }

    private static string Short(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        var enumerator =
            System.Globalization.StringInfo.GetTextElementEnumerator(text);

        var result = new StringBuilder();
        int count = 0;

        while (enumerator.MoveNext())
        {
            if (count == 14)
                return result + "…";

            result.Append(enumerator.GetTextElement());
            count++;
        }

        return result.ToString();
    }

    private static bool TrySendYandexAppCommand(string action)
    {
        int command = action switch
        {
            "previous" => 12,  // APPCOMMAND_MEDIA_PREVIOUSTRACK
            "playpause" => 14, // APPCOMMAND_MEDIA_PLAY_PAUSE
            "next" => 11,      // APPCOMMAND_MEDIA_NEXTTRACK
            _ => 0
        };

        if (command == 0)
            return false;

        string expectedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "YandexMusic", "Яндекс Музыка.exe");

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero ||
                    !string.Equals(
                        process.MainModule?.FileName,
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                IntPtr window = process.MainWindowHandle;
                IntPtr lParam = (IntPtr)(command << 16);

                // WM_APPCOMMAND = 0x0319. Передаём команду целевому окну.
                IntPtr result = SendMessage(window, 0x0319, window, lParam);
                // Log($"WM_APPCOMMAND action={action}, hwnd=0x{window.ToInt64():X}, command={command}, result={result}");
                return true;
            }
            catch (Exception e)
            {
                // Log("WM_APPCOMMAND: " + e.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        // Log("Окно Яндекс Музыки не найдено для WM_APPCOMMAND.");
        return false;
    }

    private static void SendMediaKey(ushort virtualKey)
    {
        var input = new[]
        {
            new INPUT
            {
                Type = 1,
                Data = new INPUTUNION
                {
                    Keyboard = new KEYBDINPUT { Key = virtualKey }
                }
            },
            new INPUT
            {
                Type = 1,
                Data = new INPUTUNION
                {
                    Keyboard = new KEYBDINPUT
                    {
                        Key = virtualKey,
                        Flags = 0x0002 // KEYEVENTF_KEYUP
                    }
                }
            }
        };

        int inputSize = Marshal.SizeOf<INPUT>();
        Marshal.SetLastPInvokeError(0);
        uint sent = SendInput(
            (uint)input.Length,
            input,
            inputSize);
        int error = Marshal.GetLastPInvokeError();

        // Log($"SendInput media key 0x{virtualKey:X2}: sent={sent}/{input.Length}, INPUT size={inputSize}, Win32Error={error}");

        if (sent != input.Length)
            throw new InvalidOperationException(
                $"Windows не приняла мультимедийную клавишу 0x{virtualKey:X2}; SendInput={sent}/{input.Length}, Win32Error={error}.");
    }

    private static async Task SendRatingKey(ushort scanCode)
    {
        // Не отправляем клавишу, пока зажаты модификаторы.
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }
            .Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
        {
            throw new InvalidOperationException(
                "Отпусти Shift, Ctrl, Alt и Win перед оценкой трека.");
        }

        var windows = Process.GetProcesses()
            .Where(p =>
            {
                try
                {
                    string expectedPath = Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "Programs",
                        "YandexMusic",
                        "Яндекс Музыка.exe");

                    return p.MainWindowHandle != IntPtr.Zero &&
                           string.Equals(
                               p.MainModule?.FileName,
                               expectedPath,
                               StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            })
            .Select(p => p.MainWindowHandle)
            .Distinct()
            .ToList();

        if (windows.Count != 1)
            throw new InvalidOperationException(
                "Не удалось однозначно найти окно Яндекс Музыки.");

        IntPtr target = windows[0];
        IntPtr previous = GetForegroundWindow();
        bool minimized = IsIconic(target);

        if (minimized)
            ShowWindowAsync(target, 9); // SW_RESTORE.

        SetForegroundWindow(target);

        try
        {
            for (int i = 0; i < 10; i++)
            {
                if (GetForegroundWindow() == target)
                    break;

                await Task.Delay(50);
            }

            if (GetForegroundWindow() != target)
                throw new InvalidOperationException(
                    "Windows не разрешила активировать Яндекс Музыку.");

            var input = new[]
            {
                new INPUT
                {
                    Type = 1,
                    Data = new INPUTUNION
                    {
                        Keyboard = new KEYBDINPUT
                        {
                            Scan = scanCode,
                            Flags = 0x0008 // KEYEVENTF_SCANCODE.
                        }
                    }
                },
                new INPUT
                {
                    Type = 1,
                    Data = new INPUTUNION
                    {
                        Keyboard = new KEYBDINPUT
                        {
                            Scan = scanCode,
                            Flags = 0x0008 | 0x0002 // SCANCODE | KEYUP.
                        }
                    }
                }
            };

            uint sent = SendInput(
                (uint)input.Length,
                input,
                Marshal.SizeOf<INPUT>());

            if (sent != input.Length)
                throw new InvalidOperationException(
                    "Windows не приняла нажатие клавиши.");

            await Task.Delay(120);

            // Успешная отправка клавиши НЕ подтверждает,
            // что приложение действительно изменило оценку.
        }
        finally
        {
            // Не перехватываем фокус, если пользователь
            // уже самостоятельно переключился в другое окно.
            if (GetForegroundWindow() == target)
            {
                if (minimized)
                    ShowWindowAsync(target, 6); // SW_MINIMIZE.

                if (previous != IntPtr.Zero &&
                    previous != target &&
                    IsWindow(previous))
                {
                    SetForegroundWindow(previous);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Key;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint count, INPUT[] inputs, int size);
}