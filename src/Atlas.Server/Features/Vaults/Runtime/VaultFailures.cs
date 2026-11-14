using System.Net.Sockets;
using Atlas.Server.Features.Vaults.Kdbx;

namespace Atlas.Server.Features.Vaults.Runtime;

/// <summary>
/// Czech texts for the ways a vault file can fail. They are fixed guidance: no path, password, key or file content,
/// so they may be stored in Vault.LastFailure, returned by the API and logged.
/// </summary>
public static class VaultFailures
{
    public const string NoStoredPassword = "Heslo k trezoru není uložené nebo ho server už nedokáže dešifrovat. Zadejte hlavní heslo znovu.";
    public const string KeyChanged = "Uložené heslo už soubor neotevře; hlavní heslo bylo pravděpodobně změněno v KeePassu. Zadejte aktuální hlavní heslo.";
    public const string WrongKey = "Heslo tento soubor neotevře. Podporované je jen hlavní heslo; databáze s klíčovým souborem nebo účtem Windows otevřít nelze.";
    public const string Corrupt = "Soubor je poškozený, neúplný nebo to není databáze KeePass.";
    public const string Unsupported = "Soubor používá formát nebo ochranu, kterou Atlas nečte (KDBX 3.1, klíčový soubor, Twofish). Uložte ho v KeePassu 2.35 nebo novějším jako KDBX 4 chráněný jen hlavním heslem.";
    public const string LimitExceeded = "Soubor překračuje povolené limity trezoru (velikost, počet záznamů nebo náročnost odvození klíče).";
    public const string FileMissing = "Soubor trezoru v umístění nebyl nalezen.";
    public const string Unavailable = "Umístění trezoru není dostupné. Zkontrolujte síťové připojení, sdílenou složku a přihlašovací údaje umístění.";
    public const string LocationPassword = "Heslo umístění nelze dešifrovat. Zadejte ho znovu v nastavení umístění.";

    public static string Describe(KdbxFailure failure, bool storedKey) => failure switch
    {
        KdbxFailure.KeyChanged => KeyChanged,
        KdbxFailure.WrongKey => storedKey ? KeyChanged : WrongKey,
        KdbxFailure.Unsupported => Unsupported,
        KdbxFailure.LimitExceeded => LimitExceeded,
        _ => Corrupt,
    };

    /// <summary>Storage problems that make a vault temporarily unavailable rather than broken.</summary>
    public static bool IsUnavailable(Exception error) =>
        error is IOException or UnauthorizedAccessException or TimeoutException or SocketException or ArgumentException;

    public static string DescribeUnavailable(Exception error) => error switch
    {
        FileNotFoundException => FileMissing,
        _ => Unavailable,
    };
}
