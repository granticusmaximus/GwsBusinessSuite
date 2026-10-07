# Intelligence sources (researched 2026-10-04) - APPROVED 2026-10-04, all 7 items, DONE 2026-10-04

> **~~CLOSED~~ (audit 2026-10-07):** all 7 items built and verified 2026-10-04.

Sources reviewed: https://github.com/0xh3xa/awesome-cyber-security-tools and https://infosec.house
(threat-intel + OSINT sections). Grant approved the whole list ("Start work on the intelligence
additions", 2026-10-04).

## Build status

| # | Item | Status |
| --- | --- | --- |
| 1 | CISA KEV feed | done |
| 2 | CVE detail (NVD + EPSS + KEV) | done |
| 3 | abuse.ch feeds | done |
| 4 | IP exposure (InternetDB, Tor exit, Spamhaus DROP) | done |
| 5 | Domain exposure (CT subdomains via certspotter - crt.sh was down 502 every try; urlscan page.domain; Wayback first/last) | done |
| 6 | HIBP recent public breaches | done |
| 7 | Automation nodes (threatintel.knownExploited / lookupCve / checkIndicator) | done |

Design: new services + result types (no change to DomainIntelResult's constructor). Live shapes
captured 2026-10-04: KEV not sorted by dateAdded; EPSS values are strings; NVD metrics under
cvssMetricV40/V31/V30/V2; urlhaus tags = array|null, threatfox tags = comma string; bazaar CSV
'", "'-separated with # comments; Spamhaus DROP = NDJSON with a trailing metadata line;
InternetDB answers for private IPs (skip them); urlscan `domain:` matches any contacted domain
(use `page.domain:`); certspotter unauthenticated limit 10/window (cache per domain); Wayback CDX
limit=-1 timed out for github.com - latest snapshot uses the availability API instead.
Verified live in the browser 2026-10-04 (all panels, CVE-2024-3400, 1.1.1.1, github.com) and by the
enforced responsive audit at 360/820/1280.

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
