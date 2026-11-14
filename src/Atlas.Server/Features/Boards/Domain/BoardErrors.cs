namespace Atlas.Server.Features.Boards.Domain;

/// <summary>The answers of the board API. None of them repeats the input.</summary>
public static class BoardErrors
{
    public static ApiException NotFound()
    {
        return new ApiException(404, "Tabule nebyla nalezena.");
    }

    public static ApiException ReadOnly()
    {
        return new ApiException(403, "K úpravám tabule potřebujete v prostoru oprávnění k zápisu.");
    }

    public static ApiException Rejected(string code)
    {
        return code switch
        {
            BoardRejection.ItemExists => new ApiException(409, "Prvek s tímto identifikátorem už existuje.", code),
            BoardRejection.ItemMissing => new ApiException(409, "Prvek na tabuli neexistuje.", code),
            BoardRejection.ItemDeleted => new ApiException(409, "Prvek byl smazán; obnovte ho, nebo změnu zahoďte.", code),
            _ => new ApiException(400, "Změna tabule není platná.", BoardRejection.Invalid)
        };
    }

    public static ApiException TooLarge()
    {
        return new ApiException(400, "Tabule by byla příliš velká (nejvýše 4 MiB textu a komponent); obsah rozdělte do více tabulí.", "board_too_large");
    }

    public static ApiException OperationConflict()
    {
        return new ApiException(409, "Operace s tímto identifikátorem už proběhla s jinými změnami.", "operation_conflict");
    }

    public static ApiException ResyncRequired()
    {
        return new ApiException(409, "Od známé verze přibylo příliš mnoho změn; načtěte tabuli znovu.", "resync_required");
    }
}
