---
name: review-connection-url-catalog
description: Review the connection URL builder catalog when changing provider schemes, aliases, URL parameters, endpoints, defaults, authentication, TLS, or driver selection.
---

# Review the connection URL catalog

Find the affected profile in the provider's `Mapping/ConnectionUrls/` directory. Shared ODBC profiles live alongside the ODBC mappers. Profiles for the external OLE DB and ADOMD packages live in `connection-urls/`. The catalog index in `connection-urls/catalog-source.json` groups the profiles in website display order and owns reusable definitions.

Compare the profile with the registered mapper and driver behavior. Review schemes, endpoint forms, defaults, driver options, authentication, and TLS that the change affects. Update the fragment and the relevant contract tests when the builder's user-visible behavior changes. The catalog composer checks schemes against provider registrations and derives the alias and capability metadata.

Run the focused `ConnectionUrlCatalogTest` tests and the relevant provider tests. See `connection-urls/README.md` for generation and release commands.
