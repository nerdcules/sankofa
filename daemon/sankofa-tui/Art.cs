namespace Sankofa.Tui;

/// <summary>
/// The sankofa bird of the Adinkra symbols: walking forward, its head turned back over its body.
/// "Go back and fetch it": what was left behind. Drawn in half blocks from the symbol's
/// silhouette, one character per pixel across and two pixels down, so the shape keeps its
/// proportions in a terminal.
/// </summary>
static class Art
{
    public const string Bird = """
                 ▄▄▄████▄▄▄
              ▄█████████████▄
            ▄████▀▀▀ ▀▀██████▄
           ████▀         ▀▀███
         ▄████               ▀▀
         █████
        ▄█████                ▄
        ██████▄            ▄▄██▄█▄
        ▀████████▄▄▄  ▄▄▄▄████████
         ████████████████████████
         ▀██████████████████████▀
           ███████████████████▀
            ▀▀██████████████▀
               ▀▀█████████
                █▀      ▀█▄
               █▀         █▄
            ████          ▀█▄▄▄
            ▀              ▀▀▀
        """;

    public const string Motto = "sankofa: go back and fetch it";
}
