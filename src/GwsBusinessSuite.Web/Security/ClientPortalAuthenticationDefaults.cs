namespace GwsBusinessSuite.Web.Security;

// A completely separate cookie authentication scheme from the internal staff admin login
// (CookieAuthenticationDefaults.AuthenticationScheme) and its MFA-pending scheme
// (MfaAuthenticationDefaults.PendingScheme) - a client-portal session must never be able to
// authorize against an admin policy, or vice versa, so they don't share a cookie or a scheme.
public static class ClientPortalAuthenticationDefaults
{
    public const string Scheme = "GwsClientPortal";
    public const string ContactIdClaim = "gws-client-portal:contact-id";
}
