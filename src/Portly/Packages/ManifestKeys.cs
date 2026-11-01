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

        public const string PublicKey1 = "<RSAKeyValue><Modulus>uqWRpUIoK3ZIChUEFctCbj/+VxnO7hKakN5hLo2mo/oOMpvI97n/R0r9WZ4vXfbb6WH2H+k54ot5zAuyEO/BtxAT7a4018HcTsqVkSkFhJG32oe2RN5O+gOncaEjRtIEQVlSJQR4pSmBbdX3pnOSODLoKrNxtZK2q0SKkU7oFkSg21J4xRvNW4oCTj37VqrZciAzfoN9fQq31PuLdznO2sLhnaHYtUxTpkKdkyzd9Be5TAI6XJBwSSI7FYmOeP+6vGpmkhIMdZI9gkIplUPGYwooDQf65pgzzU+6U9wampG3CzYtQABRV5948Em220AjFYY6GX5NOarkAJv/QviK64CI4qQCbIo4v3ytISfqrpS7H13XHO1HtDD9nrUSFmi0DuQ0e+IgcC8cLIZoyALLzyL9NBHr3u8St1uIgLcvTuDhChoUGqTXAMnqetOEdDpniS7cG4A4RKSUn7b10q4x/w19aQmD9xVwR+U5GIj9hiFOCNH/WpWvopSOHnYzB9Jh</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        public static readonly string[] Trusted = { PublicKey1 };
    }
}
