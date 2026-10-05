# Threat Intelligence — User Guide

Threat Intelligence (`/admin/threat-intel`) is one page of independent panels: subscribed threat
pulses from AlienVault OTX, a domain/IP investigation tool, CISA's Known Exploited
Vulnerabilities with on-demand CVE detail, recent public data breaches, live malware
infrastructure from abuse.ch, and defend.network's daily threat briefings. Everything comes from
free, public sources. Only the OTX panel and the investigation tool's Focsec IP-reputation check
need a (free, self-registered) key; everything else works with no key at all. Each panel loads on
its own, so a slow source never holds up the rest of the page.

This guide is text-only (no screenshots) — see the note at the end of
[`docs/USER_GUIDES.md`](USER_GUIDES.md) for why.

## Contents

1. [OTX threat pulses](#otx-threat-pulses)
2. [Domain / IP investigation](#domain--ip-investigation)
3. [Known exploited vulnerabilities and CVE lookup](#known-exploited-vulnerabilities-and-cve-lookup)
4. [Recent data breaches](#recent-data-breaches)
5. [Malware infrastructure](#malware-infrastructure)
6. [defend.network threat briefings](#defendnetwork-threat-briefings)
7. [Automation nodes](#automation-nodes)
8. [Who can see this](#who-can-see-this)
9. [Known limitations](#known-limitations)

## OTX threat pulses

The top-left panel lists pulses (community-curated threat reports, each bundling a description,
tags, and a count of the indicators — malicious IPs, domains, file hashes — it contains) from
AlienVault OTX. With no search term, it shows your account's own subscribed pulses; type a
keyword and press the search button to instead search OTX's public pulse index. Click a pulse's
title to open the full report on OTX's own site. This panel is blank, with a plain "not
configured" message, until a free OTX API key is added — see `ThreatIntelOptions.cs` for the
signup link.

## Domain / IP investigation

Type a domain name (e.g. `example.com`) or an IP address (e.g. `8.8.8.8`) and click
**Investigate**. For a domain, you'll see its registration record (registrar, registration and
expiry dates, nameservers, status codes), its A/AAAA DNS records, and — if it's currently serving
a website — its live TLS certificate (subject, issuer, validity window, and every hostname the
certificate covers). For an IP address, you'll see its registration record instead (the
allocating organization, e.g. "GOOGLE") and, if a free Focsec API key is configured, whether it's
currently flagged as a VPN, proxy, Tor exit node, or bot. Registration lookups use RDAP (the
modern WHOIS successor) and DNS/TLS lookups use nothing but this app's own server — none of that
needs any key. Any part of the lookup that fails (a domain with no live web server, a TLD whose
registry isn't reachable) shows a plain warning for just that part rather than failing the whole
lookup.

Below that, an **Exposure** section adds what the wider internet can see (no keys needed):

- **For an IP:** open ports, hostnames and known CVEs from Shodan's InternetDB (click a CVE to
  open it in the CVE panel); whether it's a current **Tor exit node** (the Tor Project's own exit
  list); and whether it sits in a hijacked or criminal netblock on **Spamhaus DROP**. Private and
  reserved addresses (10.x, 192.168.x and so on) are skipped - public sources know nothing about
  them.
- **For a domain:** every hostname that has appeared in public **certificate-transparency logs**
  (a good way to spot forgotten subdomains), the most recent public **urlscan.io** scans of its
  pages, and when the **Wayback Machine** first and last archived it. You can paste a full URL;
  only its domain is used.

## Known Exploited Vulnerabilities and CVE lookup

Lists the CVEs most recently added to CISA's **Known Exploited Vulnerabilities** catalog - flaws
CISA has confirmed attackers are actively exploiting - with a badge when ransomware groups use
them. Click a CVE, or type one into the box (e.g. `CVE-2024-3400`) and press **Look up**, for its
detail: the NVD description and CVSS score, its **EPSS** score (FIRST's estimate of the chance it
is exploited in the next 30 days, with its percentile among all CVEs), and, if it is on the KEV
list, CISA's required action and federal due date.

## Recent Data Breaches

The newest breaches published by **Have I Been Pwned**: the company, how many accounts, what kinds
of data leaked, when it happened and when it was published. Click a name for HIBP's write-up.
Unverified breaches are labelled; fabricated breaches and spam lists are left out. This panel only
shows company-level facts - it never looks up or stores anyone's personal data.

## Malware Infrastructure

Live data from **abuse.ch**, in four tabs: **Botnet C2** servers (Feodo Tracker), **Malicious
URLs** currently distributing malware (URLhaus), **Indicators** of compromise such as C2
addresses (ThreatFox), and recent malware **Samples** with their SHA-256 hashes (MalwareBazaar).

These are real, active threats. Addresses and links are shown **defanged** - `hxxp://` for
`http://` and `[.]` for dots - and are never clickable, so they can't be opened by accident. Don't
visit them. The **report** links go to abuse.ch's own page about each item, which is safe.

## defend.network Threat Briefings

The bottom panel lists defend.network's recent daily threat briefings — real vulnerabilities,
exploits, and campaigns currently being tracked, each with a severity badge and topic tags. Click
a briefing's title to read the full write-up on defend.network. No configuration needed.

## Automation nodes

Three read-only nodes in the automation editor's **Data** category use the same sources:

- **Known Exploited Vulnerabilities** - CVEs CISA added in the last *sinceDays* days. Schedule it
  daily, add an **If** on `vulnerabilityCount` greater than 0, then **Notify**, and you get an
  email whenever CISA adds one.
- **Look Up CVE** - the CVE detail above for any id, e.g. one taken from an earlier node.
- **Check Domain / IP Exposure** - the Exposure section above for a domain or IP.

See the [Automation guide](AUTOMATION_USER_GUIDE.md) for how to build a workflow.

## Who can see this

Threat Intelligence requires the **AdminOnly** policy, same as the rest of the Intelligence
cluster.

## Known limitations

- **OTX pulses and Focsec's IP reputation check are both optional, key-gated features.** A fresh
  deployment with neither key configured still shows RDAP/DNS/TLS domain lookups and
  defend.network's briefings — only those two specific panels stay blank until their own free,
  self-registered key is added.
- **Some free sources are rate-limited or occasionally slow.** Certificate-transparency lookups
  (SSLMate certspotter - crt.sh, the usual choice, was down throughout development) allow only a
  few lookups per hour without an account, so results are cached for six hours per domain and a
  rate-limited lookup says so. The Wayback Machine sometimes doesn't answer within 15 seconds; that
  part is then skipped with a note.
- **Feeds refresh on a cache, not live.** abuse.ch data refreshes every 30 minutes, the KEV catalog
  every 3 hours and the breach list every 6 hours.
- **Feodo Tracker's botnet list is short** - it only tracks a few botnet families, most of which
  have been taken down, so a handful of entries is normal.
- **RDAP lookups depend on `rdap.org`'s own redirect table**, which doesn't cover every TLD's
  registry — a lookup for a TLD it doesn't know how to redirect returns a plain "not found" result
  rather than a registration record, even for a domain that's genuinely registered.
- **DNS lookups only return A/AAAA records.** .NET's built-in DNS resolver has no public API for
  other record types (MX, TXT, etc.) without adding a third-party resolver library, which this
  page deliberately doesn't do — those record types simply aren't shown.
- **The TLS certificate panel connects without validating the certificate**, on purpose — an
  expired, self-signed, or hostname-mismatched certificate is itself a real, useful finding for an
  investigation tool, so it's shown rather than rejected. This tool never sends or receives real
  data over that connection beyond the handshake itself.
