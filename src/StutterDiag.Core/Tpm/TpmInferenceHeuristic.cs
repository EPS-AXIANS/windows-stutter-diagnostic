using StutterDiag.Core.Model;

namespace StutterDiag.Core.Tpm;

/// <summary>
/// Best-effort "firmware vs discrete" inference. Windows does not report this, so the
/// result is always a heuristic and the returned <see cref="TpmInferenceResult.Basis"/>
/// spells out what it was based on. When the inputs are ambiguous the result is
/// <see cref="TpmType.Undetermined"/> — never a guess dressed up as a fact.
/// </summary>
public static class TpmInferenceHeuristic
{
    // TCG-registered vendor ids whose TPMs are, in practice, integrated into the SoC / chipset.
    private static readonly HashSet<string> FirmwareVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "AMD", "INTC", "INTEL", "MSFT", "QCOM", "QUALCOMM"
    };

    // Vendors that ship physically discrete TPM packages.
    private static readonly HashSet<string> DiscreteVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "IFX", "INFINEON", "STM", "STMICRO", "NTC", "NUVOTON", "IBM", "BRCM", "BROADCOM",
        "ATML", "ATMEL", "NSM", "SNS", "SINOSUN", "TXN", "GOOG", "GOOGLE", "ROCC", "FLYS"
    };

    public static TpmInferenceResult Infer(
        bool present,
        string? manufacturerName,
        string? manufacturerIdRaw,
        string? interfaceType,
        bool? discreteDeviceOnLpcOrSpiBus)
    {
        if (!present)
            return new TpmInferenceResult(TpmType.NotDetected,
                "Windows reports no usable TPM (Win32_Tpm absent or TBS returned no device).");

        if (discreteDeviceOnLpcOrSpiBus == true)
            return new TpmInferenceResult(TpmType.Discrete,
                "A dedicated TPM device is enumerated on the LPC/SPI bus, which indicates a discrete TPM chip.");

        string? key = FirstNonEmpty(manufacturerName, manufacturerIdRaw)?.Trim().Trim('\0');

        if (key is not null && FirmwareVendors.Contains(key))
            return new TpmInferenceResult(TpmType.Firmware,
                $"Manufacturer id '{key}' belongs to a CPU/SoC vendor whose TPM is normally firmware-based (fTPM / PTT / Pluton). " +
                "No discrete TPM device was found on the LPC/SPI bus. This is a heuristic, not a value reported by Windows.");

        if (key is not null && DiscreteVendors.Contains(key))
            return new TpmInferenceResult(TpmType.Discrete,
                $"Manufacturer id '{key}' belongs to a vendor that ships discrete TPM packages. This is a heuristic, not a value reported by Windows.");

        string extra = string.IsNullOrWhiteSpace(interfaceType) ? "" : $" Interface type reported as '{interfaceType}'.";
        return new TpmInferenceResult(TpmType.Undetermined,
            $"A TPM is present but its type cannot be determined: manufacturer id '{key ?? "unknown"}' is not in the known " +
            $"firmware/discrete tables and no discrete device was enumerated on the LPC/SPI bus.{extra}");
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public sealed record TpmInferenceResult(TpmType Type, string Basis);
