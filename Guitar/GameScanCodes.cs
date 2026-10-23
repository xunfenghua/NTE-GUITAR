namespace MidiAutoPlayer.Guitar;

/// <summary>
/// 游戏键盘按键 ScanCode 映射: 游戏键 1 对应最低音弦，键 6 对应最高音弦。
/// 内部物理吉他弦号仍按标准编号: 1 弦最高音 E4，6 弦最低音 E2。
/// </summary>
public static class GameScanCodes
{
    public const ushort Digit1 = 0x02; // 游戏键 1 -> 最低音 E2 / 现实 6 弦
    public const ushort Digit2 = 0x03; // 游戏键 2 -> A2 / 现实 5 弦
    public const ushort Digit3 = 0x04; // 游戏键 3 -> D3 / 现实 4 弦
    public const ushort Digit4 = 0x05; // 游戏键 4 -> G3 / 现实 3 弦
    public const ushort Digit5 = 0x06; // 游戏键 5 -> B3 / 现实 2 弦
    public const ushort Digit6 = 0x07; // 游戏键 6 -> 最高音 E4 / 现实 1 弦

    public const ushort KeyR = 0x13;   // 上一页 (ScanCode 0x13)
    public const ushort KeyT = 0x14;   // 下一页 (ScanCode 0x14)
    public const ushort KeyZ = 0x2C;   // 吉他模式切换

    /// <summary>
    /// 将现实吉他琴弦编号 (1..6, 1=最高音E4, 6=最低音E2) 转换为游戏物理按键 ScanCode
    /// 规则：游戏键 1 是最低音弦 (现实 6 弦, E2)，游戏键 6 是最高音弦 (现实 1 弦, E4)
    /// </summary>
    public static ushort ForGuitarString(int physicalStringNumber) => physicalStringNumber switch
    {
        1 => Digit6, // 物理 1弦 (高E 64) -> 游戏按键 6 (0x07)
        2 => Digit5, // 物理 2弦 (B3 59) -> 游戏按键 5 (0x06)
        3 => Digit4, // 物理 3弦 (G3 55) -> 游戏按键 4 (0x05)
        4 => Digit3, // 物理 4弦 (D3 50) -> 游戏按键 3 (0x04)
        5 => Digit2, // 物理 5弦 (A2 45) -> 游戏按键 2 (0x03)
        6 => Digit1, // 物理 6弦 (低E 40) -> 游戏按键 1 (0x02)
        _ => Digit1
    };

    /// <summary>
    /// 将现实琴弦编号 (1..6) 转换为游戏按键数字 (1..6)
    /// 物理 1 弦 -> 游戏键 6；物理 6 弦 -> 游戏键 1
    /// </summary>
    public static int GetGameDigit(int physicalStringNumber) =>
        (physicalStringNumber >= 1 && physicalStringNumber <= 6) ? (7 - physicalStringNumber) : 1;

    /// <summary>
    /// 修复旧版启动迁移写出的游戏键升序数组。内部数组按现实物理弦序 1(高音)..6(低音) 存储，
    /// 因此标准 ScanCode 顺序必须是游戏键 6..1。自定义非标准映射保持原样。
    /// </summary>
    public static ushort[] NormalizeLegacyStringScanCodes(ushort[]? scanCodes)
    {
        if (scanCodes == null)
        {
            return [Digit6, Digit5, Digit4, Digit3, Digit2, Digit1];
        }

        if (scanCodes.Length == 6 && scanCodes.SequenceEqual(new ushort[] { Digit1, Digit2, Digit3, Digit4, Digit5, Digit6 }))
        {
            return [Digit6, Digit5, Digit4, Digit3, Digit2, Digit1];
        }

        return scanCodes;
    }
}
