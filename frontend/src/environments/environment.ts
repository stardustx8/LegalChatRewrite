export const environment = {
  // Local Azure Functions host (adjust if your port differs)
  // Use proxy for API calls during local development
  baseUrl: '',
  // Optional alternate base for admin endpoints (upload/cleanup). If empty, falls back to baseUrl.
  adminBase: '',
  // Azure AD SSO configuration (development)
  aad: {
    enabled: false,
    tenantId: 'YOUR_TENANT_ID',
    clientId: 'YOUR_APP_CLIENT_ID',
    // If empty, AuthService will default to window.location.origin + '/'
    redirectUri: '',
    scopes: ['User.Read']
  }
};
