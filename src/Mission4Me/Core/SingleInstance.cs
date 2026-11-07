using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mission4Me.Core;

/// <summary>
/// Comunicação entre instâncias. Se o app já está aberto e alguém o abre de novo:
///  • normal / --missao → pede à instância principal que abra a janela de missão
///  • --sair            → pede à instância principal que feche de forma limpa (usado pelo build.bat)
/// </summary>
public static class SingleInstance
{
    private const string OpenEventName = @"Local\Mission4Me.OpenMission";
    private const string ExitEventName = @"Local\Mission4Me.Exit";
    private static EventWaitHandle? _open, _exit;
    private static RegisteredWaitHandle? _openWait, _exitWait;

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    public static void Listen(Action onOpen, Action onExit)
    {
        _open = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
        _exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _openWait = ThreadPool.RegisterWaitForSingleObject(_open, (_, _) => onOpen(), null, Timeout.Infinite, false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => onExit(), null, Timeout.Infinite, true);
    }

    public static void SignalOpen()
    {
        AllowSetForegroundWindow(-1); // deixa a instância principal trazer a janela para frente
        Signal(OpenEventName);
    }

    public static void SignalExit() => Signal(ExitEventName);

    private static void Signal(string name)
    {
        try { if (EventWaitHandle.TryOpenExisting(name, out var ev)) { ev.Set(); ev.Dispose(); } }
        catch { }
    }

    public static void Stop()
    {
        _openWait?.Unregister(null);
        _exitWait?.Unregister(null);
        _open?.Dispose();
        _exit?.Dispose();
    }
}
