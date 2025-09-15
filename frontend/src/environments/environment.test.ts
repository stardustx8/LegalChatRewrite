export const environment = {
  // Test Azure Function (LegalApi) base URL; if the UI is served by this function
  // you can leave baseUrl empty to use the current origin.
  // Example: 'https://fct-euw-legalcb-legalapi-test-xxxx.westeurope-01.azurewebsites.net'
  baseUrl: '',

  // If admin APIs (upload/cleanup) are hosted on a separate Function App (LegalDocProcessor),
  // set the full URL here for the test environment; otherwise leave empty to fall back to baseUrl.
  // Example: 'https://fct-euw-legalcb-legaldocprocessor-test-xxxx.westeurope-01.azurewebsites.net'
  adminBase: '',

  // Azure AD SSO (optional in test); keep secrets out of source control
  aad: {
    enabled: false,
    tenantId: '',
    clientId: '',
    // If empty, AuthService will use window.location.origin + '/'
    redirectUri: '',
    scopes: ['User.Read']
  }
};
