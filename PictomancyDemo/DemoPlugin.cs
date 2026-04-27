using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Pictomancy;

namespace PictomancyDemo;

public sealed class DemoPlugin : IDalamudPlugin
{
    private const string CommandName = "/pctdemo";

    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IObjectTable Objects { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem _windowSystem = new("PictomancyDemo");
    private DemoWindow? _window;
    private PctContext? _pctCtx;

    public DemoPlugin()
    {
        Log.Information("[PictomancyDemo] ctor: services injected");
        try
        {
            _pctCtx = PctService.Initialize(PluginInterface);
            Log.Information("[PictomancyDemo] PctService.Initialize OK");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PictomancyDemo] PctService.Initialize failed; demo will run without drawing");
        }

        _window = new DemoWindow();
        _windowSystem.AddWindow(_window);

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Pictomancy demo window.",
        });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;

        _windowSystem.RemoveAllWindows();
        _window?.Dispose();
        _pctCtx?.Dispose();
    }

    private void OnCommand(string command, string args) => OpenWindow();
    private void OpenWindow() { if (_window != null) _window.IsOpen = true; }

    private void OnDraw()
    {
        try
        {
            _windowSystem.Draw();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PictomancyDemo] WindowSystem.Draw threw");
        }

        if (_pctCtx != null && _window?.IsOpen == true && _window.WorldDrawEnabled)
        {
            try
            {
                _window.DrawWorld();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PictomancyDemo] DrawWorld threw");
                _window.WorldDrawEnabled = false;
            }
        }
    }
}
