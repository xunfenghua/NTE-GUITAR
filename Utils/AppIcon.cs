using System.Drawing;

namespace MidiAutoPlayer.Utils;

public static class AppIcon
{
    private static Icon? _icon;

    public static Icon Value
    {
        get
        {
            if (_icon == null)
            {
                try
                {
                    string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                    if (File.Exists(icoPath))
                    {
                        _icon = new Icon(icoPath);
                    }
                    else
                    {
                        _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
                    }
                }
                catch
                {
                    _icon = SystemIcons.Application;
                }
            }
            return _icon;
        }
    }
}
