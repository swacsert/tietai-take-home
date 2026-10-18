using System.Security.Cryptography;
using System.Text;

namespace Part2.Ingest;

/// <summary>Small rules shared by the HL7 and the FHIR mappers.</summary>
internal static class Normalization
{
    /// <summary>
    /// Returns the canonical name of a diagnosis coding system. HL7 "I10" formally means ICD-10, but the
    /// codes in the feed are ICD-10-CM. Unknown systems pass through unchanged: the concept mapper then
    /// finds no mapping for them and the writer loads concept_id 0.
    /// </summary>
    public static string CodeSystem(string sourceSystem)
    {
        if (sourceSystem == "I10" || sourceSystem == "http://hl7.org/fhir/sid/icd-10-cm")
        {
            return "ICD10CM";
        }

        return sourceSystem;
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of the text.</summary>
    public static string Sha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }
}
