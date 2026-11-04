using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace winuitest.Controls;

/// <summary>A native button whose link action is also apparent to mouse users.</summary>
public sealed class HandButton : Button
{
    private static readonly InputCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    public HandButton()
    {
        ProtectedCursor = HandCursor;
        IsEnabledChanged += (_, _) => ProtectedCursor = IsEnabled ? HandCursor : null;
    }
}
