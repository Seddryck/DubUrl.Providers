# SSAS Multidimensional QA infrastructure

The live contract expects an SSAS Multidimensional instance on `localhost` with the AdventureWorks cube available as `multidim/AdventureWorks`.

The GitHub-hosted Windows job installs SQL Server 2025 Enterprise Developer Analysis Services and restores the matching Microsoft AdventureWorks backup before running the live contract. For a local run, provision the same named instance and catalog first.
