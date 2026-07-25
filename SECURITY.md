# Security and safety

SDAT runs for the current Windows user. It stores authoritative state in local SQLite, creates only named SDAT Task Scheduler entries, and does not use accounts, telemetry, mandatory cloud services, or a privileged background service.

Power execution is fail-safe:

- task, notification, and overlay actions carry a schedule id and revision;
- stale or superseded activations do nothing;
- corrupt, unavailable, or forward-version databases block power actions;
- backups are verified before they are considered restorable;
- v1 migration imports only recognized state and exact legacy task signatures;
- cancellation attempts Windows' emergency countdown abort before loading the database or scheduler path.

Shutdown and restart force applications to close. Save work before scheduling them. Windows controls the final system countdown and notification delivery policy.

To report a vulnerability, use GitHub's private security advisory flow for this repository. Do not include sensitive machine data in a public issue.
