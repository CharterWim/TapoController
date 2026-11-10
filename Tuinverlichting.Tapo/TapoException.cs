namespace Tuinverlichting.Tapo;

/// Fout van een Tapo-lamp of van het TPAP-protocol.
public class TapoException(string message, int? errorCode = null, bool sessionInvalid = false) : Exception(message)
{
    /// error_code van de lamp, als die er was.
    public int? ErrorCode { get; } = errorCode;

    /// Fouten waarbij doorgaan (opnieuw inloggen, andere lampen proberen) het account/de lampen kan blokkeren.
    public bool IsCredentialProblem => !IsSessionInvalid &&
        (ErrorCode is -1501 or -2101 or -2203 or -40401 || Message.Contains("dev_confirm"));

    /// De lamp kent de sessie niet meer (of het datakanaal faalde): opnieuw inloggen helpt.
    public bool IsSessionInvalid { get; } = sessionInvalid;
}
