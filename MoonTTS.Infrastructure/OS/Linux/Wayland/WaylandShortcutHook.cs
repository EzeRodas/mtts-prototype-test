using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;
using Tmds.DBus.Protocol;

namespace MoonTTS.Infrastructure.OS.Linux.Wayland;

/* File Structure:
        - InitializeAsync() : Task
        - RegisterShortcutAsync(ShortcutCombo? toggleShortcut, Dictionary<string,ShortcutCombo> phraseShortcuts) : Task
        - BuildPortalShortcutId(string logicalId, string trigger) : string
        - ExtractLogicalShortcutId(string portalShortcutId) : string
        - SendRequestAsync(Func<string, Task<string>> portalAction, int timeoutMs) : Task<Dictionary<string, VariantValue>>
        - FormatForWayland(ShortcutCombo combo) : string
        - CleanupSessionAsync() : Task
        - Dispose() : void
*/

/* This class is in charge of registering the global shortcuts on Wayland though the freedesktop protocol.
   Of course as Wayland does not support this, we're actually talking to the Desktop Environment (as far as I
   understand). So for distributions that use KDE or other DE that supports freedesktop, it should work. if your
   DE doesn't support it, I'm sorry but this is as much as Wayland allows to do. */

/* While yes, I do understand that global shortcuts can be used maliciously by bad actors and blah blah blah,
   I'd love to have a little chat with the Wayland people and know if they ever tried to make an app that's
   not a calculator. Next part of this rant when I'll have to implement the global positioning method. */

public class WaylandShortcutHook : IGlobalShortcutHook
{
    private const string AppId = "com.moontts.app";
    private const string TriggerSeparator = "__";
    
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private DBusConnection? _connection;
    private IDisposable? _activationWatcher;
    private string? _sessionHandle;
    
    /* Event for triggering shortcut actions */
    public event Action<string>? OnShortcutTriggered;

    /* Initializes connection to DBus session and sibscribes to shortcut activation signals */
    public async Task InitializeAsync()
    {
        string? address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            Console.WriteLine("[WaylandShortcutHook] DBusAddress.Session is null or empty.");
            return;
        }

        _connection = new DBusConnection(address);
        await _connection.ConnectAsync();
        Console.WriteLine($"[WaylandShortcutHook] Connected to D-Bus. UniqueName: {_connection.UniqueName}");

        /* Registers the app ID with the portal registry (required when launched outside an IDE/launcher) */
        try
        {
            await _connection.CallMethodAsync(CreateRegisterMessage(_connection));
            Console.WriteLine($"[WaylandShortcutHook] Registered AppId '{AppId}' with portal registry.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WaylandShortcutHook] Registry.Register warning: {ex.Message}");
        }

        /* Subscribes to GlobalShortcuts activated signal filtered strictly to the desktop portal and active session */
        _activationWatcher = await _connection.WatchSignalAsync<(string SessionPath, string ShortcutId)>(
            sender: "org.freedesktop.portal.Desktop",
            path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.portal.GlobalShortcuts",
            signal: "Activated",
            reader: (Message msg, object? _) =>
            {
                var reader = msg.GetBodyReader();
                string sessionPath = reader.ReadObjectPath().ToString();
                string shortcutId = reader.ReadString();
                return (sessionPath, shortcutId);
            },
            handler: notification =>
            {
                if (notification.Type == NotificationType.Value
                    && !string.IsNullOrEmpty(notification.Value.ShortcutId)
                    && string.Equals(notification.Value.SessionPath, _sessionHandle, StringComparison.Ordinal))
                {
                    string logicalId = ExtractLogicalShortcutId(notification.Value.ShortcutId);
                    OnShortcutTriggered?.Invoke(logicalId);
                }
                else if (notification.Exception != null)
                {
                    Console.Error.WriteLine($"[WaylandShortcutHook] Activation watcher error: {notification.Exception}");
                }
            },
            flags: ObserverFlags.None,
            emitOnCapturedContext: false);

        Console.WriteLine("[WaylandShortcutHook] Listening for Activated signals.");
        return;

        static MessageBuffer CreateRegisterMessage(DBusConnection conn)
        {
            using var regWriter = conn.GetMessageWriter();
            regWriter.WriteMethodCallHeader(
                destination: "org.freedesktop.portal.Desktop",
                path: "/org/freedesktop/portal/desktop",
                @interface: "org.freedesktop.host.portal.Registry",
                member: "Register",
                signature: "sa{sv}");

            regWriter.WriteString(AppId);
            var dictStart = regWriter.WriteDictionaryStart();
            regWriter.WriteDictionaryEnd(dictStart);

            return regWriter.CreateMessage();
        }
    }
    
    /* Creates a portal session and binds shortcuts */
    public async Task RegisterShortcutAsync(ShortcutCombo? toggleShortcut, Dictionary<string, ShortcutCombo> phraseShortcuts)
    {
        if (_connection == null)
            throw new InvalidOperationException("Wayland hook not initialized.");

        await _sessionLock.WaitAsync();
        try
        {
            await CleanupSessionAsync();

            string sessionToken = $"moontts_sess_{Guid.NewGuid():N}";
            
            /* Creates session */
            Console.WriteLine("[WaylandShortcutHook] Calling CreateSession...");
            var createResult = await SendRequestAsync(
                reqToken => _connection.CallMethodAsync(
                    CreateSessionMessage(_connection, sessionToken, reqToken),
                    (Message msg, object? _) => msg.GetBodyReader().ReadObjectPath().ToString()));

            if (!createResult.TryGetValue("session_handle", out var sessionHandleVariant))
                throw new InvalidOperationException("Portal CreateSession did not return a session_handle.");

            _sessionHandle = sessionHandleVariant.Type == VariantValueType.ObjectPath
                ? sessionHandleVariant.GetObjectPath().ToString()
                : sessionHandleVariant.GetString();
            
            Console.WriteLine($"[WaylandShortcutHook] Session created: {_sessionHandle}");
            
            /* Builds shortcut bindings using trigger-scoped IDs so updated triggers are applied by the portal */
            var shortcutsToBind = new List<(string Id, string Description, string Trigger)>();

            if (toggleShortcut != null)
            {
                string trigger = FormatForWayland(toggleShortcut);
                shortcutsToBind.Add(
                    (BuildPortalShortcutId("toggle_app", trigger), "Show/Hide Moon-TTS", trigger));
            }

            foreach (var phrase in phraseShortcuts)
            {
                string trigger = FormatForWayland(phrase.Value);
                shortcutsToBind.Add(
                    (BuildPortalShortcutId($"phrase_{phrase.Key}", trigger), $"Moon-TTS Phrase: {phrase.Key}", trigger));
            }

            if (shortcutsToBind.Count == 0) return;
            
            Console.WriteLine($"[WaylandShortcutHook] Calling BindShortcuts with {shortcutsToBind.Count} shortcut(s): " +
                              string.Join(", ", shortcutsToBind.Select(s => $"{s.Id}={s.Trigger}")));

            await SendRequestAsync(
                reqToken => _connection.CallMethodAsync(
                    CreateBindShortcutsMessage(_connection, _sessionHandle, shortcutsToBind, reqToken),
                    (Message msg, object? _) => msg.GetBodyReader().ReadObjectPath().ToString()),
                timeoutMs: 60000);

            Console.WriteLine("[WaylandShortcutHook] BindShortcuts succeeded");
        }
        finally
        {
            _sessionLock.Release();
        }
        
        static MessageBuffer CreateSessionMessage(DBusConnection conn, string sessToken, string reqToken)
        {
            using var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.portal.Desktop",
                path: "/org/freedesktop/portal/desktop",
                @interface: "org.freedesktop.portal.GlobalShortcuts",
                member: "CreateSession",
                signature: "a{sv}");

            var start = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("session_handle_token");
            writer.WriteVariantString(sessToken);

            writer.WriteDictionaryEntryStart();
            writer.WriteString("handle_token");
            writer.WriteVariantString(reqToken);
            writer.WriteDictionaryEnd(start);

            return writer.CreateMessage();
        }

        static MessageBuffer CreateBindShortcutsMessage(
            DBusConnection conn,
            string sessionHandle,
            List<(string Id, string Description, string Trigger)> shortcuts,
            string reqToken)
        {
            using var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.portal.Desktop",
                path: "/org/freedesktop/portal/desktop",
                @interface: "org.freedesktop.portal.GlobalShortcuts",
                member: "BindShortcuts",
                signature: "oa(sa{sv})sa{sv}");

            writer.WriteObjectPath(sessionHandle);

            var arrayStart = writer.WriteArrayStart(DBusType.Struct);
            foreach (var item in shortcuts)
            {
                writer.WriteStructureStart();
                writer.WriteString(item.Id);

                var propsStart = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString("description");
                writer.WriteVariantString(item.Description);

                if (!string.IsNullOrEmpty(item.Trigger))
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("preferred_trigger");
                    writer.WriteVariantString(item.Trigger);
                }
                writer.WriteDictionaryEnd(propsStart);
            }
            writer.WriteArrayEnd(arrayStart);

            writer.WriteString(""); /* parent_window */

            var optStart = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("handle_token");
            writer.WriteVariantString(reqToken);
            writer.WriteDictionaryEnd(optStart);

            return writer.CreateMessage();
        }
    }

    /* Appends a sanitized trigger token to the shortcut ID so portal backends honor changed triggers */
    private static string BuildPortalShortcutId(string logicalId, string trigger)
    {
        string sanitizedTrigger = trigger.Replace('+', '_');
        return $"{logicalId}{TriggerSeparator}{sanitizedTrigger}";
    }

    /* Strips the trigger suffix from a portal shortcut ID before notifying listeners */
    private static string ExtractLogicalShortcutId(string portalShortcutId)
    {
        int sepIndex = portalShortcutId.IndexOf(TriggerSeparator, StringComparison.Ordinal);
        return sepIndex >= 0 ? portalShortcutId[..sepIndex] : portalShortcutId;
    }
    
    /* Subscribes to Request.Response, executes the action, and waits for the response */
    private async Task<Dictionary<string, VariantValue>> SendRequestAsync(
        Func<string, Task<string>> portalAction,
        int timeoutMs = 10000)
    {
        string reqToken = $"moontts_req_{Guid.NewGuid():N}";
        string sender = (_connection!.UniqueName ?? "").Replace(":", "").Replace(".", "_");
        string expectedReqPath = $"/org/freedesktop/portal/desktop/request/{sender}/{reqToken}";

        var tcs = new TaskCompletionSource<Dictionary<string, VariantValue>>();
        
        using var sub = await _connection
            .WatchSignalAsync<(uint ResponseCode, Dictionary<string, VariantValue> Results)>(
                sender: null,
                path: expectedReqPath,
                @interface: "org.freedesktop.portal.Request",
                signal: "Response",
                reader: (Message msg, object? _) =>
                {
                    var r = msg.GetBodyReader();
                    uint response = r.ReadUInt32();
                    var results = r.ReadDictionaryOfStringToVariantValue();
                    return (response, results);
                },
                handler: notification =>
                {
                    if (notification.Type == NotificationType.Value)
                    {
                        Console.WriteLine($"[WaylandShortcutHook] Portal Response received: code={notification.Value.ResponseCode}");
                        if (notification.Value.ResponseCode == 0)
                            tcs.TrySetResult(notification.Value.Results);
                        else
                            tcs.TrySetException(new InvalidOperationException(
                                $"Portal request failed with code: {notification.Value.ResponseCode}"));
                    }
                    else if (notification.Exception != null)
                    {
                        Console.WriteLine($"[WaylandShortcutHook] Request signal exception: {notification.Exception}");
                        tcs.TrySetException(notification.Exception);
                    }
                },
                flags: ObserverFlags.None,
                emitOnCapturedContext: false);
        
        string actualReqPath = await portalAction(reqToken);
        if (actualReqPath != expectedReqPath)
        {
            Console.WriteLine($"[WaylandShortcutHook] WARNING: Path mismatch! Expected: {expectedReqPath}, Actual: {actualReqPath}");
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        return await tcs.Task.WaitAsync(cts.Token);
    }

    /* Applies the expected formatting for Wayland / GTK */
    private string FormatForWayland(ShortcutCombo combo)
    {
        var parts = new List<string>();

        if (combo.HasCtrl) parts.Add("CTRL");
        if (combo.HasAlt) parts.Add("ALT");
        if (combo.HasShift) parts.Add("SHIFT");
        if (combo.HasWin) parts.Add("LOGO");

        string rawKey = combo.MainKey.Trim();
        string key = rawKey.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => "Return",
            "ESC" or "ESCAPE" => "Escape",
            "SPACE" => "space",
            "TAB" => "Tab",
            "BACKSPACE" => "BackSpace",
            "DELETE" or "DEL" => "Delete",
            "INSERT" or "INS" => "Insert",
            "HOME" => "Home",
            "END" => "End",
            "PAGEUP" or "PAGE_UP" or "PGUP" => "Page_Up",
            "PAGEDOWN" or "PAGE_DOWN" or "PGDN" => "Page_Down",
            "UP" or "ARROWUP" => "Up",
            "DOWN" or "ARROWDOWN" => "Down",
            "LEFT" or "ARROWLEFT" => "Left",
            "RIGHT" or "ARROWRIGHT" => "Right",
            "PLUS" or "ADD" => "plus",
            "MINUS" or "SUBTRACT" => "minus",
            /* Function keys F1-F24 */
            var k when k.Length > 1 && k[0] == 'F' && int.TryParse(k[1..], out _) => k,
            /* Single alphanumeric character */
            var k when k.Length == 1 => k.ToLowerInvariant(),
            /* Preserve exact casing for other valid XKB keysym names */
            _ => rawKey
        };

        parts.Add(key);
        return string.Join("+", parts);
    }

    /* If a session exists, it gets closed */
    private async Task CleanupSessionAsync()
    {
        if (string.IsNullOrEmpty(_sessionHandle) || _connection == null)
            return;
        
        try
        {
            await _connection.CallMethodAsync(CreateCloseSessionMessage(_connection, _sessionHandle))
                .ConfigureAwait(false);
        }
        catch
        {
             /* Ignore if already closed */
        }
        finally
        {
            _sessionHandle = null;
        }

        static MessageBuffer CreateCloseSessionMessage(DBusConnection conn, string sessionHandle)
        {
            using var writer = conn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.portal.Desktop",
                path: sessionHandle,
                @interface: "org.freedesktop.portal.Session",
                member: "Close",
                signature: "");

            return writer.CreateMessage();
        }
    }

    /* Disposes the listener and closes any active portal session with a bounded timeout so shutdown cannot deadlock */
    public void Dispose()
    {
        _activationWatcher?.Dispose();
        try
        {
            Task.Run(CleanupSessionAsync).Wait(500);
        }
        catch
        {
            /* Ignore errors during shutdown */
        }
        _connection?.Dispose();
        _sessionLock.Dispose();
    }
}
