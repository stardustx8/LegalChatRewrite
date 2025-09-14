export const environment = {
  baseUrl: '',
  // Optional alternate base for admin endpoints (upload/cleanup). If empty, falls back to baseUrl.
  // Set this to the LegalDocProcessor Function App URL if admin APIs are hosted separately.
  adminBase: 'https://fct-euw-legalcb-legaldocprocessor-prod-e6cxa7fbddenexgf.westeurope-01.azurewebsites.net',
  // Enable SSO in production
  aad: {
    enabled: false,
    tenantId: 'REPLACE_WITH_TENANT_ID',
    clientId: 'REPLACE_WITH_APP_CLIENT_ID',
    redirectUri: '', // defaults to window.location.origin if left empty
    scopes: ['User.Read']
  }
};
