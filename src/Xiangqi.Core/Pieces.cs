using System.Runtime.CompilerServices;

namespace Xiangqi.Core;

public enum Color : byte
{
    Red = 0,
    Black = 1
}

public enum PieceType : byte
{
    None = 0,
    General = 1,
    Advisor = 2,
    Elephant = 3,
    Horse = 4,
    Chariot = 5,
    Cannon = 6,
    Soldier = 7
}

/// <summary>
/// Colored piece. Values 1–7 are red and match <see cref="PieceType"/>;
/// black pieces are those values plus 7.
/// </summary>
public enum Piece : byte
{
    None = 0,
    RedGeneral = 1,
    RedAdvisor = 2,
    RedElephant = 3,
    RedHorse = 4,
    RedChariot = 5,
    RedCannon = 6,
    RedSoldier = 7,
    BlackGeneral = 8,
    BlackAdvisor = 9,
    BlackElephant = 10,
    BlackHorse = 11,
    BlackChariot = 12,
    BlackCannon = 13,
    BlackSoldier = 14
}

public static class PieceExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color Opposite(this Color color) => color == Color.Red ? Color.Black : Color.Red;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color GetColor(this Piece piece)
        => (byte)piece >= (byte)Piece.BlackGeneral ? Color.Black : Color.Red;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PieceType GetPieceType(this Piece piece)
    {
        int value = (byte)piece;
        if (value >= (byte)Piece.BlackGeneral)
            value -= 7;
        return (PieceType)value;
    }

    public static char ToFenChar(this Piece piece) => piece switch
    {
        Piece.RedGeneral => 'K',
        Piece.RedAdvisor => 'A',
        Piece.RedElephant => 'B',
        Piece.RedHorse => 'N',
        Piece.RedChariot => 'R',
        Piece.RedCannon => 'C',
        Piece.RedSoldier => 'P',
        Piece.BlackGeneral => 'k',
        Piece.BlackAdvisor => 'a',
        Piece.BlackElephant => 'b',
        Piece.BlackHorse => 'n',
        Piece.BlackChariot => 'r',
        Piece.BlackCannon => 'c',
        Piece.BlackSoldier => 'p',
        _ => '.'
    };

    public static Piece FromFenChar(char c) => c switch
    {
        'K' => Piece.RedGeneral,
        'A' => Piece.RedAdvisor,
        'B' => Piece.RedElephant,
        'N' => Piece.RedHorse,
        'R' => Piece.RedChariot,
        'C' => Piece.RedCannon,
        'P' => Piece.RedSoldier,
        'k' => Piece.BlackGeneral,
        'a' => Piece.BlackAdvisor,
        'b' => Piece.BlackElephant,
        'n' => Piece.BlackHorse,
        'r' => Piece.BlackChariot,
        'c' => Piece.BlackCannon,
        'p' => Piece.BlackSoldier,
        _ => throw new FormatException($"Unknown Xiangqi piece '{c}'.")
    };
}
