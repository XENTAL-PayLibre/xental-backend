using Xental.Api.Contracts;

namespace Xental.Api.Banking;

/// <summary>
/// Curated list of Nigerian banks with their NIP codes (the same scheme Nomba uses for
/// bank lookups and transfers). Lets the dashboard present a bank by name and send the code
/// behind the scenes, so users never have to know a numeric code. Sorted by name.
/// </summary>
public static class NigerianBanks
{
    public static readonly IReadOnlyList<BankResponse> All =
    [
        new("Access Bank", "000014"),
        new("Access Bank (Diamond)", "000005"),
        new("ALAT by Wema", "000017"),
        new("Citibank Nigeria", "000009"),
        new("Ecobank Nigeria", "000010"),
        new("Fidelity Bank", "000007"),
        new("First Bank of Nigeria", "000016"),
        new("First City Monument Bank (FCMB)", "000003"),
        new("Globus Bank", "000027"),
        new("Guaranty Trust Bank (GTBank)", "000013"),
        new("Heritage Bank", "000020"),
        new("Keystone Bank", "000002"),
        new("Kuda Bank", "090267"),
        new("Lotus Bank", "000029"),
        new("Moniepoint MFB", "090405"),
        new("OPay", "100004"),
        new("PalmPay", "100033"),
        new("Parallex Bank", "000030"),
        new("Polaris Bank", "000008"),
        new("Premium Trust Bank", "000031"),
        new("Providus Bank", "000023"),
        new("Stanbic IBTC Bank", "000012"),
        new("Standard Chartered Bank", "000021"),
        new("Sterling Bank", "000001"),
        new("SunTrust Bank", "000022"),
        new("TAJ Bank", "000026"),
        new("Titan Trust Bank", "000025"),
        new("Union Bank of Nigeria", "000018"),
        new("United Bank for Africa (UBA)", "000004"),
        new("Unity Bank", "000011"),
        new("Wema Bank", "000017"),
        new("Zenith Bank", "000015"),
    ];
}
