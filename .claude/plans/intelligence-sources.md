# Intelligence sources proposal (researched 2026-10-04) - AWAITING GRANT'S APPROVAL

Sources reviewed: https://github.com/0xh3xa/awesome-cyber-security-tools and https://infosec.house
(threat-intel + OSINT sections). Nothing here is built yet; build only what Grant approves.

Already integrated (Threat Intel page): defend.network briefings, RDAP/DNS/TLS domain lookups,
AlienVault OTX pulses (free key), Focsec IP reputation (free key).

## Proposed (all free; "no key" = verified 2026-10-04 with a live request)

| # | Addition | Source | Key | Where |
| --- | --- | --- | --- | --- |
| 1 | Known Exploited Vulnerabilities feed - newly added CVEs CISA says are exploited in the wild, with due dates and ransomware-use flag | CISA KEV JSON | none | Threat Intel |
| 2 | CVE detail on demand: description, CVSS (NVD), exploit-probability score (FIRST EPSS), "on KEV?" badge | NVD CVE API 2.0, FIRST EPSS API, CIRCL CVE search | none (NVD rate-limited without a key) | Threat Intel |
| 3 | Live malware infrastructure: active botnet C2 servers, recent malicious URLs, recent IOCs, recent malware samples (hash, family) | abuse.ch Feodo Tracker / URLhaus / ThreatFox / MalwareBazaar bulk exports | none (bulk exports; their query APIs now want a free Auth-Key) | Threat Intel |
| 4 | IP investigation additions: open ports + known CVEs for an IP; Tor exit node check; Spamhaus DROP (hijacked netblocks) check | Shodan InternetDB, Tor bulk exit list, Spamhaus DROP JSON | none | Domain/IP investigation panel |
| 5 | Domain investigation additions: certificate-transparency subdomains; recent urlscan.io scans of the domain; first/last archived snapshot | crt.sh, urlscan.io public search, Wayback CDX | none (crt.sh and Wayback are flaky - returned 502/timeout during research; needs caching + graceful "unavailable") | Domain/IP investigation panel |
| 6 | Recent public data breaches (company, date, record count, data types) | Have I Been Pwned /breaches (public list only) | none | Threat Intel or News Intelligence |
| 7 | Threat-intel automation nodes (KEV new item, IOC lookup) so workflows can alert on them | items 1-4 | none | Automation |

## Deliberately excluded

- People-search/data-broker sites, breach dumps (Dehashed, Snusbase, PSBDMP), phone/address
  lookups - personal data, not appropriate to aggregate.
- Social-media scrapers (Sherlock, Osintgram, WhatsMyName...) - Grant's standing "no social
  scraping" directive.
- Tor ransomware leak sites, onion directories/search engines, pastebins - illicit content.
- Offensive tooling (C2, cracking, exploits, default-password lists, scanners like Nmap) - out of
  scope for a defensive intelligence view; active scanning of third parties is also legally risky.
- Commercial platforms (Recorded Future, ThreatConnect, Maltego) and self-hosted platforms
  (MISP, OpenCTI, IntelOwl) - paid or require running separate servers.
- File/malware analysis tools (YARA, sandboxes, decompilers) - local desktop tools, not feeds.
