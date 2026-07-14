# Security and safety

SDAT runs only for the current Windows user. It creates named Task Scheduler entries, stores local configuration, and does not use telemetry, accounts, cloud services, or a background service.

Scheduled shutdown and restart actions currently force applications to close. Save work before scheduling them. Every one-time action can be canceled with `sdat cancel`; `sdat cancel all` also removes the daily schedule.

To report a vulnerability, use GitHub's private security advisory flow for this repository. Do not include sensitive machine data in a public issue.
