namespace Portly.Packages
{
    /// <summary>
    /// Public keys that may sign the manifests. Only public halves belong here; the private key is the
    /// PORTLY_MANIFEST_KEY secret of the release workflows and never enters the repository.
    ///
    /// To set the key, replace the value of PublicKey1 on its line below with the output of the commands in
    /// docs/03-yayinlama.md ("Manifest imzalama"): the single-line RSA public key in XML form, as a quoted string.
    /// Until then nothing verifies, so remote manifests are refused and tools/sign-manifests.ps1 fails a release.
    ///
    /// Rotation: add the new key as PublicKey2 and list it in Trusted, release that version, and remove the old key
    /// only from a later release, once clients carrying the new key are out. Clients that know only the old key keep
    /// working as long as the .sig files also hold a signature by that key (see PORTLY_MANIFEST_KEY_2).
    /// </summary>
    public static class ManifestKeys
    {
        public const string Placeholder = "PASTE_RSA3072_PUBLIC_KEY_XML_HERE";

        public const string PublicKey1 = Placeholder;

        public static readonly string[] Trusted = { PublicKey1 };
    }
}
