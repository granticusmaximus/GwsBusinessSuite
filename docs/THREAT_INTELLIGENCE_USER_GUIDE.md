# Threat Intelligence — User Guide

Threat Intelligence (`/admin/threat-intel`) is one page with three independent panels: subscribed
threat pulses from AlienVault OTX, a domain/IP investigation tool, and defend.network's daily
threat briefings. All three pull from free, publicly-provided sources — two need a free,
self-registered API key to turn on; the third and the investigation tool's registration/DNS/TLS
lookups work with no key at all.

This guide is text-only (no screenshots) — see the note at the end of
[`docs/USER_GUIDES.md`](USER_GUIDES.md) for why.

## Contents

1. [OTX threat pulses](#otx-threat-pulses)
2. [Domain / IP investigation](#domain--ip-investigation)
3. [defend.network threat briefings](#defendnetwork-threat-briefings)
4. [Who can see this](#who-can-see-this)
5. [Known limitations](#known-limitations)

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

## defend.network Threat Briefings

The bottom panel lists defend.network's recent daily threat briefings — real vulnerabilities,
exploits, and campaigns currently being tracked, each with a severity badge and topic tags. Click
a briefing's title to read the full write-up on defend.network. No configuration needed.

## Who can see this

Threat Intelligence requires the **AdminOnly** policy, same as the rest of the Intelligence
cluster.

## Known limitations

- **OTX pulses and Focsec's IP reputation check are both optional, key-gated features.** A fresh
  deployment with neither key configured still shows RDAP/DNS/TLS domain lookups and
  defend.network's briefings — only those two specific panels stay blank until their own free,
  self-registered key is added.
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
