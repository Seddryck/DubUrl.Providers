# SSAS Tabular QA infrastructure

The live contract expects an SSAS Tabular instance on `localhost` with the AdventureWorks model available as `tabular/AdventureWorks`.

The GitHub-hosted Windows job installs SQL Server 2025 Enterprise Developer Analysis Services and restores the matching Microsoft AdventureWorks backup before running the live contract. For a local run, provision the same named instance and catalog first.
