# Threat Intelligence — User Guide

Threat Intelligence (`/admin/threat-intel`) has two tabs. **Live intel** is one page of
independent panels: a single search box that investigates any IP, domain, URL, file hash or CVE,
a shared investigation history, subscribed threat pulses from AlienVault OTX, CISA's Known
Exploited Vulnerabilities with on-demand CVE detail, recent public data breaches, live malware
infrastructure from abuse.ch, and defend.network's daily threat briefings. **My exposure** watches
what you own: your domains and servers, the software you run, this app's own packages, and your
email security, with a daily digest email. Everything comes from
free, public sources. Only the OTX panel and the investigation tool's Focsec IP-reputation check
need a (free, self-registered) key; everything else works with no key at all. Each panel loads on
its own, so a slow source never holds up the rest of the page.

This guide is text-only (no screenshots) — see the note at the end of
[`docs/USER_GUIDES.md`](USER_GUIDES.md) for why.

## Contents

1. [Investigate](#investigate)
2. [Investigation history and Sentinel export](#investigation-history-and-sentinel-export)
3. [OTX threat pulses](#otx-threat-pulses)
4. [Known exploited vulnerabilities and CVE lookup](#known-exploited-vulnerabilities-and-cve-lookup)
5. [Recent data breaches](#recent-data-breaches)
6. [Malware infrastructure](#malware-infrastructure)
7. [defend.network threat briefings](#defendnetwork-threat-briefings)
8. [My exposure](#my-exposure)
9. [Automation nodes](#automation-nodes)
10. [Who can see this](#who-can-see-this)
11. [Known limitations](#known-limitations)

## OTX threat pulses

The panel next to the history lists pulses (community-curated threat reports, each bundling a description,
tags, and a count of the indicators — malicious IPs, domains, file hashes — it contains) from
AlienVault OTX. With no search term, it shows your account's own subscribed pulses; type a
keyword and press the search button to instead search OTX's public pulse index. Click a pulse's
title to open the full report on OTX's own site. This panel is blank, with a plain "not
configured" message, until a free OTX API key is added (see [Adding API keys](#adding-api-keys)).

## Investigate

Paste anything into the search box at the top and click **Investigate**. It works out what you
typed and runs the lookups that apply:

- **IP address or domain** (e.g. `8.8.8.8`, `example.com`) - everything below, plus a check of
  every recent abuse.ch entry.
- **URL** - the same, for the URL's host, plus whether that exact URL is on URLhaus.
- **File hash** (MD5, SHA-1 or SHA-256) - whether MalwareBazaar or ThreatFox has seen the file
  recently, with the malware family.
- **CVE id** (e.g. `CVE-2024-3400`) - the CVE detail described under the KEV panel.

Defanged input from reports (`hxxp://`, `example[.]com`) is accepted. Anything found in a threat
feed is listed first under **Found in threat feeds**, defanged, with a link to abuse.ch's report;
otherwise the page says it isn't in the abuse.ch feeds. The feeds are abuse.ch's *recent*
exports (roughly the last month), so "not found" means "not recently reported", not "safe".

For a domain, you'll see its registration record (registrar, registration and
expiry dates, nameservers, status codes), its DNS records (A/AAAA plus CNAME, MX, NS, TXT and
CAA), and — if it's currently serving
a website — its live TLS certificate (subject, issuer, validity window, and every hostname the
certificate covers). For an IP address, you'll see its registration record instead (the
allocating organization, e.g. "GOOGLE") and, if a free Focsec API key is configured, whether it's
currently flagged as a VPN, proxy, Tor exit node, or bot. Registration lookups use RDAP (the
modern WHOIS successor), asking the registry IANA lists for that TLD or address range first and
rdap.org second. Address lookups use this server's own resolver and the other record types use
DNS-over-HTTPS (Cloudflare, then Google). None of that needs any key. Any part of the lookup that fails (a domain with no live web server, a TLD whose
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

## Investigation history and Sentinel export

Every investigation is saved to **Investigation history**, shared by all admins, with a one-line
summary (the last 200 are kept). Click one to reopen exactly what that lookup found at the time -
it isn't re-run. **Export to Sentinel** turns the open investigation into a Sentinel page
("Investigation: ..."), with malicious addresses written defanged; afterwards the button becomes
**Open in Sentinel**. The trash icon removes an entry from the history (an exported Sentinel page
stays).

## Known Exploited Vulnerabilities and CVE lookup

Lists the CVEs most recently added to CISA's **Known Exploited Vulnerabilities** catalog - flaws
CISA has confirmed attackers are actively exploiting - with a badge when ransomware groups use
them. Click a CVE, or type one into the box (e.g. `CVE-2024-3400`) and press **Look up**, for its
detail: the NVD description and CVSS score, its **EPSS** score (FIRST's estimate of the chance it
is exploited in the next 30 days, with its percentile among all CVEs), and, if it is on the KEV
list, CISA's required action and federal due date.

The list covers the whole catalog, 25 rows at a time. Filter it by vendor, product, name or CVE
id; switch on **Ransomware only**; or switch on **New since my last visit** - entries CISA added
on or after the day you last opened this page (your own marker, per admin; nothing is "new" on
your very first visit). **Highest EPSS first** sorts by exploit probability, loading scores for
every row that passes the filters. Shown rows carry their EPSS score as a badge.

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

## My exposure

The **My exposure** tab watches things you own. Nothing runs until someone opens this tab once;
after that a background job checks watched domains and IPs every 12 hours, and software and
packages once a day. **Run all checks now** runs everything immediately (it takes a minute or two
because the NVD search waits between requests).

**Findings** at the top lists what the checks found, most severe first. There are two kinds:

- **States** - an expiring certificate, a missing DMARC record, a vulnerable package. These
  resolve themselves on the next check after you fix them. **Acknowledge** records that someone
  has seen it, but it stays listed until it's fixed.
- **Events** - something that happened: a new certificate issued for one of your hostnames, a new
  open port, a DNS change, a newly published CVE matching your software. These never resolve on
  their own; **Acknowledge** closes them.

Switch on **Show resolved** to see closed ones too.

### Watched domains & IPs

Add a domain (e.g. `grantwatson.dev`) or a public IP (e.g. your server's). Private addresses are
refused - internet sources can't see them. Each check looks for:

- **Domains:** TLS certificate expiring within 30 days (or expired), domain registration expiring
  within 30 days, the domain appearing in abuse.ch feeds (a sign it's compromised or being
  impersonated), new hostnames in certificate-transparency logs, A/AAAA records changing, and the
  email security problems below.
- **IPs:** listing on Spamhaus DROP, being a Tor exit node, appearing in abuse.ch feeds, CVEs
  Shodan associates with the exposed software, and newly opened ports.

The first check of a new asset records a baseline, so its existing hostnames and ports don't show
up as "new".

### Email security check

Enter any mail domain and click **Check** for a letter grade (A to F) and its SPF, DMARC, DKIM,
MTA-STS and TLS-RPT setup, with a plain explanation of each problem. Watched domains are graded
automatically and their low/medium/high/critical problems become findings. A domain with no mail
servers is only asked to publish `v=spf1 -all` and a DMARC record, so nobody can send mail as it.
DKIM keys can only be found on known selector names (Google's `google`, Microsoft's `selector1`
and so on); a custom selector shows as "no key on common selectors".

### Software you run

Add the software your business runs - or click a suggestion for this app's own stack (.NET,
Ollama, Docker, Cloudflare Tunnel, SQLite, coturn). Each item is checked daily against:

- **CISA KEV entries from the last 12 months** - matched on the vendor/product you give (or the
  name, if you give neither). These are confirmed exploited in the wild.
- **High-severity CVEs (CVSS 7+) published in the last 30 days** whose NVD description matches
  your keywords (the name, if you give none). Keyword matches can include other products that
  merely mention yours, and severity is capped at high because a match doesn't prove your version
  is affected - read the detail before acting.

### Daily digest & packages

Turn on **Email me a daily digest**, enter an address and pick an hour. Once a day after that hour
the digest emails every new finding plus the vulnerabilities CISA added since the last digest; a
day with nothing new is skipped. **Send now** sends one immediately. It uses the server's normal
email delivery (Settings > Email).

**This app's packages** lists every NuGet package in the running build plus the browser libraries
it loads. They're checked daily against OSV.dev (GitHub Security Advisories); a vulnerable one
becomes a finding with the version that fixes it, and the finding resolves once the package is
upgraded and deployed.

## Automation nodes

Three read-only nodes in the automation editor's **Data** category use the same sources:

- **Known Exploited Vulnerabilities** - CVEs CISA added in the last *sinceDays* days. Schedule it
  daily, add an **If** on `vulnerabilityCount` greater than 0, then **Notify**, and you get an
  email whenever CISA adds one.
- **Look Up CVE** - the CVE detail above for any id, e.g. one taken from an earlier node.
- **Check Domain / IP Exposure** - the Exposure section above for a domain or IP.

See the [Automation guide](AUTOMATION_USER_GUIDE.md) for how to build a workflow.

## Adding API keys

Keys are configuration, never code - they don't go in any source file. Sign up for a free key at
[otx.alienvault.com](https://otx.alienvault.com) (OTX pulses) or [focsec.com](https://focsec.com)
(IP reputation), then:

- **Production:** add `THREAT_INTEL_OTX_API_KEY=...` and/or `THREAT_INTEL_FOCSEC_API_KEY=...` to
  the server's `.env`, then run `docker compose up -d --force-recreate gwssuite`. Nothing is
  deleted; the container just restarts with the new setting.
- **Local development:** `dotnet user-secrets set "ThreatIntel:OtxApiKey" "<key>" --project
  src/GwsBusinessSuite.Web` (or `ThreatIntel:FocsecApiKey`). Stored in your user profile, outside
  the repository.

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
- **Some TLDs publish no registration data at all.** `.io` and `.co`, for example, have no RDAP
  service (and `.io` no WHOIS server either), so their domains show "The .io registry doesn't
  publish registration data over RDAP" instead of a registration record.
- **The TLS certificate panel connects without validating the certificate**, on purpose — an
  expired, self-signed, or hostname-mismatched certificate is itself a real, useful finding for an
  investigation tool, so it's shown rather than rejected. This tool never sends or receives real
  data over that connection beyond the handshake itself.
- **NVD allows 5 requests per 30 seconds without a key**, so the software check spaces its
  searches 7 seconds apart. Each item uses up to 5 keywords.
- **Package checks only see what the build declares.** NuGet packages come from the running app's
  dependency manifest; browser libraries come from a list kept in `DependencyAdvisoryService.cs`,
  which a test keeps in step with every pinned jsDelivr reference.
