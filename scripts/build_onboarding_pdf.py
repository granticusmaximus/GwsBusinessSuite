#!/usr/bin/env python3
import sys
import zlib
import re

class PDFBuilder:
    def __init__(self, filename="docs/INTELLIGENCE_ONBOARDING_GUIDE.pdf"):
        self.filename = filename
        self.objects = []
        self.page_refs = []
        self.current_page_stream = []
        self.width = 612.0  # Letter
        self.height = 792.0
        self.margin_left = 50.0
        self.margin_right = 50.0
        self.margin_top = 54.0
        self.margin_bottom = 54.0
        self.content_width = self.width - self.margin_left - self.margin_right
        self.cursor_y = self.height - self.margin_top
        self.pages_data = [] # list of (stream_bytes)
        self.page_number = 1

    def add_object(self, data):
        self.objects.append(data)
        return len(self.objects)

    def escape_pdf(self, text):
        return text.replace('\\', '\\\\').replace('(', '\\(').replace(')', '\\)')

    def start_new_page(self):
        if self.current_page_stream:
            # Add footer to current page before saving
            self.draw_footer()
            stream_content = "\n".join(self.current_page_stream).encode('latin1', 'replace')
            self.pages_data.append(stream_content)
            self.current_page_stream = []
            self.page_number += 1
        
        self.cursor_y = self.height - self.margin_top
        # Draw header if page > 1
        if self.page_number > 1:
            self.draw_header()

    def draw_header(self):
        y = self.height - 36
        line = [
            "q",
            "0.4 0.45 0.52 rg",
            "BT",
            "/F1 8 Tf",
            f"{self.margin_left} {y} Td",
            f"({self.escape_pdf('Sentinel Intelligence — User Onboarding Guide (Overwatch & World News)')}) Tj",
            "ET",
            "0.85 0.88 0.92 RG",
            "0.5 w",
            f"{self.margin_left} {y - 4} m {self.width - self.margin_right} {y - 4} l S",
            "Q"
        ]
        self.current_page_stream.extend(line)

    def draw_footer(self):
        y = 36
        line = [
            "q",
            "0.85 0.88 0.92 RG",
            "0.5 w",
            f"{self.margin_left} {y + 12} m {self.width - self.margin_right} {y + 12} l S",
            "0.4 0.45 0.52 rg",
            "BT",
            "/F1 8 Tf",
            f"{self.margin_left} {y} Td",
            f"({self.escape_pdf('admin.gwsapp.net · GWS Business Suite · Confidential')}) Tj",
            "ET",
            "BT",
            "/F1 8 Tf",
            f"{self.width - self.margin_right - 45} {y} Td",
            f"(Page {self.page_number}) Tj",
            "ET",
            "Q"
        ]
        self.current_page_stream.extend(line)

    def check_space(self, needed_pt):
        if self.cursor_y - needed_pt < self.margin_bottom + 15:
            self.start_new_page()

    def draw_title_banner(self, eyebrow, title, subtitle):
        self.check_space(110)
        # Eyebrow
        self.current_page_stream.extend([
            "q",
            "0.49 0.23 0.93 rg",  # Purple
            "BT",
            "/F2 9 Tf",
            f"{self.margin_left} {self.cursor_y} Td",
            f"({self.escape_pdf(eyebrow.upper())}) Tj",
            "ET",
            "Q"
        ])
        self.cursor_y -= 16

        # Title
        self.current_page_stream.extend([
            "q",
            "0.06 0.09 0.16 rg",  # Dark slate
            "BT",
            "/F2 20 Tf",
            f"{self.margin_left} {self.cursor_y} Td",
            f"({self.escape_pdf(title)}) Tj",
            "ET",
            "Q"
        ])
        self.cursor_y -= 22

        # Subtitle
        self.current_page_stream.extend([
            "q",
            "0.32 0.38 0.46 rg",  # Muted slate
            "BT",
            "/F3 10.5 Tf",
            f"{self.margin_left} {self.cursor_y} Td",
            f"({self.escape_pdf(subtitle)}) Tj",
            "ET",
            "Q"
        ])
        self.cursor_y -= 18

        # Decorative bar
        self.current_page_stream.extend([
            "q",
            "0.49 0.23 0.93 rg",
            f"{self.margin_left} {self.cursor_y} 40 2.5 re f",
            "0.85 0.88 0.92 rg",
            f"{self.margin_left + 45} {self.cursor_y + 0.5} {self.content_width - 45} 1 re f",
            "Q"
        ])
        self.cursor_y -= 20

    def draw_h1(self, text):
        self.check_space(45)
        self.cursor_y -= 8
        self.current_page_stream.extend([
            "q",
            # Accent bar
            "0.49 0.23 0.93 rg",
            f"{self.margin_left} {self.cursor_y - 2} 4 14 re f",
            # Text
            "0.06 0.09 0.16 rg",
            "BT",
            "/F2 13.5 Tf",
            f"{self.margin_left + 10} {self.cursor_y} Td",
            f"({self.escape_pdf(text)}) Tj",
            "ET",
            # Bottom dividing line
            "0.88 0.90 0.94 RG",
            "0.75 w",
            f"{self.margin_left} {self.cursor_y - 6} m {self.width - self.margin_right} {self.cursor_y - 6} l S",
            "Q"
        ])
        self.cursor_y -= 20

    def draw_h2(self, text):
        self.check_space(35)
        self.cursor_y -= 6
        self.current_page_stream.extend([
            "q",
            "0.12 0.16 0.24 rg",
            "BT",
            "/F2 11.5 Tf",
            f"{self.margin_left} {self.cursor_y} Td",
            f"({self.escape_pdf(text)}) Tj",
            "ET",
            "Q"
        ])
        self.cursor_y -= 16

    def draw_h3(self, text):
        self.check_space(28)
        self.cursor_y -= 4
        self.current_page_stream.extend([
            "q",
            "0.25 0.32 0.42 rg",
            "BT",
            "/F2 10 Tf",
            f"{self.margin_left} {self.cursor_y} Td",
            f"({self.escape_pdf(text)}) Tj",
            "ET",
            "Q"
        ])
        self.cursor_y -= 14

    def wrap_text(self, text, max_chars):
        words = text.split()
        lines = []
        cur = []
        cur_len = 0
        for w in words:
            if cur_len + len(w) + (1 if cur else 0) <= max_chars:
                cur.append(w)
                cur_len += len(w) + (1 if len(cur) > 1 else 0)
            else:
                if cur:
                    lines.append(" ".join(cur))
                cur = [w]
                cur_len = len(w)
        if cur:
            lines.append(" ".join(cur))
        return lines

    def draw_paragraph(self, text, font="F1", size=9.0, leading=12.5, color=(0.12, 0.16, 0.24), indent=0):
        # Estimated max characters per line for Helvetica at 9pt on 504pt width
        max_chars = int((self.content_width - indent) / (size * 0.52))
        lines = self.wrap_text(text, max_chars)
        needed = len(lines) * leading + 4
        self.check_space(needed)

        r, g, b = color
        self.current_page_stream.extend([
            "q",
            f"{r} {g} {b} rg",
            "BT",
            f"/{font} {size} Tf",
            f"{leading} TL",
            f"{self.margin_left + indent} {self.cursor_y} Td"
        ])
        for i, line in enumerate(lines):
            self.current_page_stream.append(f"({self.escape_pdf(line)}) Tj")
            if i < len(lines) - 1:
                self.current_page_stream.append("T*")
        self.current_page_stream.extend([
            "ET",
            "Q"
        ])
        self.cursor_y -= (len(lines) * leading + 4)

    def draw_bullet(self, bold_prefix, text, level=1):
        indent = 14 if level == 1 else 28
        bullet_x = self.margin_left + indent - 10
        full_text = f"{bold_prefix} {text}".strip()
        max_chars = int((self.content_width - indent) / (9.0 * 0.52))
        lines = self.wrap_text(full_text, max_chars)
        needed = len(lines) * 12.5 + 3
        self.check_space(needed)

        # Draw bullet dot
        bullet_y = self.cursor_y + 3.0
        self.current_page_stream.extend([
            "q",
            "0.49 0.23 0.93 rg" if level == 1 else "0.4 0.45 0.52 rg",
            f"{bullet_x} {bullet_y} 2.5 2.5 re f",
            "Q"
        ])

        # Draw text
        self.current_page_stream.extend([
            "q",
            "0.12 0.16 0.24 rg",
            "BT",
            "/F1 9 Tf",
            "12.5 TL",
            f"{self.margin_left + indent} {self.cursor_y} Td"
        ])
        for i, line in enumerate(lines):
            self.current_page_stream.append(f"({self.escape_pdf(line)}) Tj")
            if i < len(lines) - 1:
                self.current_page_stream.append("T*")
        self.current_page_stream.extend([
            "ET",
            "Q"
        ])
        self.cursor_y -= (len(lines) * 12.5 + 3)

    def draw_callout_box(self, title, items):
        total_lines = 1
        for item in items:
            total_lines += len(self.wrap_text(item, 85))
        box_h = total_lines * 13 + 22
        self.check_space(box_h + 10)

        box_y = self.cursor_y - box_h + 6
        # Background and left border
        self.current_page_stream.extend([
            "q",
            "0.97 0.98 0.99 rg",  # Soft slate bg
            f"{self.margin_left} {box_y} {self.content_width} {box_h} re f",
            "0.88 0.91 0.95 RG",  # Border
            "0.75 w",
            f"{self.margin_left} {box_y} {self.content_width} {box_h} re S",
            "0.49 0.23 0.93 rg",  # Purple left accent bar
            f"{self.margin_left} {box_y} 3.5 {box_h} re f",
            "Q"
        ])

        text_y = self.cursor_y - 12
        # Title
        self.current_page_stream.extend([
            "q",
            "0.49 0.23 0.93 rg",
            "BT",
            "/F2 9.5 Tf",
            f"{self.margin_left + 14} {text_y} Td",
            f"({self.escape_pdf(title.upper())}) Tj",
            "ET",
            "Q"
        ])
        text_y -= 14

        # Items
        for item in items:
            lines = self.wrap_text(item, 85)
            self.current_page_stream.extend([
                "q",
                "0.18 0.22 0.30 rg",
                "BT",
                "/F1 8.5 Tf",
                "12 TL",
                f"{self.margin_left + 14} {text_y} Td"
            ])
            for k, l in enumerate(lines):
                self.current_page_stream.append(f"({self.escape_pdf(l)}) Tj")
                if k < len(lines) - 1:
                    self.current_page_stream.append("T*")
            self.current_page_stream.extend(["ET", "Q"])
            text_y -= (len(lines) * 12 + 2)

        self.cursor_y = box_y - 12

    def draw_table(self, headers, rows, col_widths):
        # Calculate height
        row_height = 18
        table_h = (len(rows) + 1) * row_height + 4
        self.check_space(table_h)

        table_top = self.cursor_y
        table_bottom = self.cursor_y - table_h

        # Header background
        self.current_page_stream.extend([
            "q",
            "0.92 0.94 0.97 rg",  # Header fill
            f"{self.margin_left} {table_top - row_height} {self.content_width} {row_height} re f",
            "0.80 0.84 0.90 RG",  # Table outer border
            "0.5 w",
            f"{self.margin_left} {table_bottom} {self.content_width} {table_h} re S",
            "Q"
        ])

        # Header text
        cur_x = self.margin_left
        for idx, h in enumerate(headers):
            w = col_widths[idx]
            self.current_page_stream.extend([
                "q",
                "0.08 0.12 0.20 rg",
                "BT",
                "/F2 8.5 Tf",
                f"{cur_x + 6} {table_top - 12} Td",
                f"({self.escape_pdf(h)}) Tj",
                "ET",
                "Q"
            ])
            cur_x += w

        # Draw rows
        for r_idx, row in enumerate(rows):
            y_pos = table_top - (r_idx + 2) * row_height
            # Alternating background
            if r_idx % 2 == 1:
                self.current_page_stream.extend([
                    "q",
                    "0.98 0.985 0.995 rg",
                    f"{self.margin_left} {y_pos} {self.content_width} {row_height} re f",
                    "Q"
                ])
            # Row dividing line
            self.current_page_stream.extend([
                "q",
                "0.88 0.91 0.94 RG",
                "0.5 w",
                f"{self.margin_left} {y_pos + row_height} m {self.width - self.margin_right} {y_pos + row_height} l S",
                "Q"
            ])
            # Row cells
            c_x = self.margin_left
            for c_idx, cell in enumerate(row):
                w = col_widths[c_idx]
                is_bold = (c_idx == 0)
                font_tag = "/F2" if is_bold else "/F1"
                font_color = "0.08 0.12 0.20 rg" if is_bold else "0.20 0.25 0.33 rg"
                self.current_page_stream.extend([
                    "q",
                    font_color,
                    "BT",
                    f"{font_tag} 8.2 Tf",
                    f"{c_x + 6} {y_pos + 5.5} Td",
                    f"({self.escape_pdf(str(cell))}) Tj",
                    "ET",
                    "Q"
                ])
                c_x += w

        self.cursor_y = table_bottom - 14

    def draw_ascii_diagram(self, lines_list):
        line_h = 11.5
        box_h = len(lines_list) * line_h + 16
        self.check_space(box_h)

        box_y = self.cursor_y - box_h + 4
        # Terminal box background
        self.current_page_stream.extend([
            "q",
            "0.08 0.11 0.16 rg",  # Dark terminal slate
            f"{self.margin_left} {box_y} {self.content_width} {box_h} re f",
            "0.18 0.24 0.35 RG",
            "0.75 w",
            f"{self.margin_left} {box_y} {self.content_width} {box_h} re S",
            "Q"
        ])

        text_y = self.cursor_y - 12
        for line in lines_list:
            color = "0.22 0.74 0.97 rg" if "STEP" in line or "->" in line else "0.85 0.90 0.95 rg"
            self.current_page_stream.extend([
                "q",
                color,
                "BT",
                "/F4 7.8 Tf",
                f"{self.margin_left + 12} {text_y} Td",
                f"({self.escape_pdf(line)}) Tj",
                "ET",
                "Q"
            ])
            text_y -= line_h

        self.cursor_y = box_y - 14

    def build_pdf(self):
        # Save last page
        if self.current_page_stream:
            self.draw_footer()
            self.pages_data.append("\n".join(self.current_page_stream).encode('latin1', 'replace'))

        # Object generation
        # 1: Catalog
        # 2: Pages
        # 3: Helvetica
        # 4: Helvetica-Bold
        # 5: Helvetica-Oblique
        # 6: Courier
        # 7: Courier-Bold
        # 8..: Page and Content objects
        output = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
        offsets = [0]  # dummy for 0-index

        num_pages = len(self.pages_data)
        page_obj_ids = []
        next_id = 8

        for i in range(num_pages):
            page_obj_ids.append(next_id)
            next_id += 2 # one for Page, one for Content

        # Obj 1: Catalog
        offsets.append(len(output))
        output.extend(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n")

        # Obj 2: Pages
        kids_str = " ".join([f"{pid} 0 R" for pid in page_obj_ids])
        offsets.append(len(output))
        output.extend(f"2 0 obj\n<< /Type /Pages /Kids [{kids_str}] /Count {num_pages} >>\nendobj\n".encode('latin1'))

        # Standard fonts
        offsets.append(len(output))
        output.extend(b"3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n")
        offsets.append(len(output))
        output.extend(b"4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n")
        offsets.append(len(output))
        output.extend(b"5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>\nendobj\n")
        offsets.append(len(output))
        output.extend(b"6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>\nendobj\n")
        offsets.append(len(output))
        output.extend(b"7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier-Bold /Encoding /WinAnsiEncoding >>\nendobj\n")

        # Pages and Content objects
        for idx, p_bytes in enumerate(self.pages_data):
            page_id = page_obj_ids[idx]
            content_id = page_id + 1

            # Page obj
            offsets.append(len(output))
            p_obj = (
                f"{page_id} 0 obj\n"
                f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {self.width} {self.height}] "
                f"/Contents {content_id} 0 R "
                f"/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R /F4 6 0 R /F5 7 0 R >> >> >>\n"
                f"endobj\n"
            ).encode('latin1')
            output.extend(p_obj)

            # Content stream obj (Flate compressed)
            comp = zlib.compress(p_bytes)
            offsets.append(len(output))
            c_obj = (
                f"{content_id} 0 obj\n"
                f"<< /Length {len(comp)} /Filter /FlateDecode >>\nstream\n"
            ).encode('latin1') + comp + b"\nendstream\nendobj\n"
            output.extend(c_obj)

        # xref
        xref_offset = len(output)
        total_objs = next_id
        output.extend(f"xref\n0 {total_objs}\n0000000000 65535 f \n".encode('latin1'))
        for off in offsets[1:]:
            output.extend(f"{off:010d} 00000 n \n".encode('latin1'))

        # trailer
        trailer = (
            f"trailer\n"
            f"<< /Size {total_objs} /Root 1 0 R >>\n"
            f"startxref\n{xref_offset}\n%%EOF\n"
        ).encode('latin1')
        output.extend(trailer)

        with open(self.filename, "wb") as f:
            f.write(output)
        print(f"Generated {self.filename} ({len(output)} bytes, {num_pages} pages).")


def build_guide():
    doc = PDFBuilder("docs/INTELLIGENCE_ONBOARDING_GUIDE.pdf")

    # Title
    doc.draw_title_banner(
        eyebrow="GWS Business Suite · Sentinel Operational Manual",
        title="Intelligence Suite: Overwatch & World News",
        subtitle="A verified, step-by-step onboarding guide for geospatial reconnaissance and global media triangulation."
    )

    doc.draw_callout_box("Authentication & Production Access Credentials", [
        "Portal Endpoint: https://admin.gwsapp.net (unauthenticated traffic redirects to /admin/login)",
        "Authorized Operator Username: turner.george.f@gmail.com",
        "Assigned Role: Administrator (unrestricted access to all Intelligence and Platform toolsets)",
        "Security Note: All session activity and intelligence pin captures are cryptographically logged."
    ])

    doc.draw_h1("1. Access & Workspace Navigation")
    doc.draw_paragraph(
        "GWS Business Suite employs a persistent dual-rail sidebar navigation model designed for high-density administrative workflows. The primary outer rail groups functional clusters, while the inner subnav expands context-specific tools without triggering disruptive full-page refreshes."
    )

    doc.draw_h2("1.1 Locating the Intelligence Cluster")
    doc.draw_paragraph(
        "To reach the Intelligence tools, click the 5th icon down on the outer navigation rail (the Radar icon, labeled 'Intel' / 'Intelligence'). This reveals the dedicated six-module subnav:"
    )

    rail_headers = ["Module Label", "Portal Route", "Icon", "Core Analytical Purpose"]
    rail_rows = [
        ["Media Watch", "/admin/news-intelligence", "bi-newspaper", "Automated feeds filtered and scored by SentinelGPT."],
        ["World News", "/admin/world-news", "bi-globe", "Global multi-lingual story clustering and agreement analysis."],
        ["Civic Watch", "/admin/government-intel", "bi-building", "Municipal and legislative open-government monitors."],
        ["BI Dashboards", "/admin/business-intel", "bi-bar-chart", "High-level pinned operational KPIs and custom reporting."],
        ["Overwatch", "/admin/osint", "bi-globe2", "3D tactical globe with DOT cameras, radar, and live news."],
        ["Threat Intelligence", "/admin/threat-intel", "bi-shield", "Exploited CVEs, malware campaigns, breaches, and IOCs."]
    ]
    doc.draw_table(rail_headers, rail_rows, [85, 115, 65, 239])

    doc.draw_h1("2. Overwatch Interface Breakdown (/admin/osint)")
    doc.draw_paragraph(
        "Overwatch is a hardware-accelerated 3D tactical globe built on CesiumJS and styled as a high-contrast terminal. It drapes high-resolution Esri World Imagery photography over elevation terrain, layered with transparent road, city, and boundary label reference vectors."
    )

    doc.draw_h2("2.1 Navigation & Globe Interactivity")
    doc.draw_bullet("Rotate & Pan:", "Left-click and drag across any area of the globe surface.")
    doc.draw_bullet("3D Tilt & Pitch:", "Hold Ctrl (or right-click) and drag vertically to toggle between 2D nadir and 3D oblique views.")
    doc.draw_bullet("Zooming:", "Scroll wheel or trackpad pinch. Calibrated to transition smoothly from whole-continent to street level.")
    doc.draw_bullet("Whole Globe Button:", "Located bottom-right (bi-globe-americas); resets to the initial continent view at 18,000 km altitude.")
    doc.draw_bullet("Time Machine Button:", "Located bottom-right (bi-clock-history); scrubs historical radar, incidents, and quakes over the past 24 hours.")
    doc.draw_bullet("Street View Person:", "Drag the person standing icon onto any roadway to launch Mapillary 360-degree street panoramas.")
    doc.draw_bullet("Live Weather HUD:", "Anchored at the bottom-left; continuously displays real-time weather station telemetry for the view center.")

    doc.draw_h2("2.2 Activating the World News Layer")
    doc.draw_paragraph(
        "Overwatch separates raw physical incidents (traffic accidents, construction) from reported media intelligence. To overlay developing global situations, open the LAYERS drawer in the top-right toolbar and locate the INTELLIGENCE group:"
    )
    doc.draw_bullet("Layer Toggle:", "Click WORLD NEWS [OFF] to switch it to WORLD NEWS [ON].")
    doc.draw_bullet("Topic Kinds:", "Filter news topics using the dedicated pill buttons: Disasters, Transport, Conflict & Unrest, and Health are active by default. Cyber, Politics, Business, and Sport can be toggled on demand.")
    doc.draw_bullet("Country Shading:", "Click SHADE COUNTRIES [ON] to shade national borders on a logarithmic purple gradient based on reporting outlet volume. Clicking a shaded country reveals its top headlines.")
    doc.draw_bullet("Time Lookback:", "Filter reporting velocity across LAST 24 HOURS (default), LAST 3 DAYS, or LAST 7 DAYS.")

    doc.draw_h2("2.3 Interpreting Purple Intelligence Pins")
    doc.draw_paragraph(
        "World News pins use a distinct Purple (#c77dff) hue so they cannot be confused with green cameras, amber traffic hazards, or pink seismic markers:"
    )

    pin_headers = ["Visual Property", "UI Appearance", "Operational Meaning"]
    pin_rows = [
        ["Marker Color", "Solid Purple (#c77dff)", "Reported international news story (unconfirmed physical incident)."],
        ["Marker Size", "12px to 30px scaling", "Scaled by total outlet count (log2 scale; larger pins = heavier coverage)."],
        ["Opacity", "100% vs 45% faded", "100% = breaking in last 48h; 45% = quiet story with no new reports."],
        ["SURGING Banner", "Pulsing size + label", "Coverage velocity doubled in the last 2 hours with 4+ new newsrooms."],
        ["Cluster Badge", "Numbered purple dot", "Groups 3+ nearby stories. Click cluster to zoom and split pins."],
        ["Placement", "City vs Country Center", "City-level if headline names city >50k; otherwise country geographic center."]
    ]
    doc.draw_table(pin_headers, pin_rows, [95, 125, 284])

    doc.draw_h2("2.4 Pin Inspection & Actionable Modal Controls")
    doc.draw_paragraph(
        "Clicking any purple pin opens a movable, resizable tactical modal panel on the globe. Every component provides deep cross-correlation with ground intelligence:"
    )
    doc.draw_bullet("Classification Banner:", "Displays CITY-LEVEL PLACEMENT or COUNTRY-LEVEL PLACEMENT · NOT A CONFIRMED INCIDENT.")
    doc.draw_bullet("Cyber Status Badge:", "For cyber topics: CONFIRMED (acknowledged), REPORTED (press only), CLAIMED (attacker claim), PRECAUTIONARY, or DISPUTED.")
    doc.draw_bullet("Headline & Summary:", "Full title, latest bulletin update (LATEST:), and a concise factual summary synthesized across outlets.")
    doc.draw_bullet("READ DOCUMENT:", "Deep-links directly to the comprehensive event intelligence dossier in World News (/admin/world-news/events/{id}).")
    doc.draw_bullet("WHO'S REPORTING:", "Renders 3D geodesic arcs from each reporting country's center to the pin (thicker arcs = more outlets).")
    doc.draw_bullet("PLAY STORY:", "Opens an interactive timeline slider with PLAY/PAUSE controls, stepping through the chronological progression of the story.")
    doc.draw_bullet("BACKED BY Sensors:", "Cross-references the story against real-world sensors within 150 miles: USGS earthquakes, NIFC wildfires, NOAA flood gauges, and ODIN grid outages.")
    doc.draw_bullet("NEARBY CAMERAS:", "Lists verified public DOT cameras within 25 miles with exact distance. Clicking any camera launches its live feed.")
    doc.draw_bullet("CAPTURE NEARBY CAMERAS:", "Initiates automated 1-hour time-lapse surveillance recording of still cameras within 10 miles, archived under AREAS > CAPTURES.")
    doc.draw_bullet("OPEN IN WORLD NEWS:", "Transitions directly to the full situation timeline and sources inside the main portal.")

    doc.draw_h1("3. World News Interface Breakdown (/admin/world-news)")
    doc.draw_paragraph(
        "World News is the primary intelligence synthesis engine. It continuously ingests feeds from 80+ country editions, consolidates multi-lingual articles reporting on the same development into Events, and links evolving Events into multi-day Situations."
    )

    doc.draw_h2("3.1 Primary Views & Ingestion Controls")
    doc.draw_bullet("Read Now Button:", "Located top-right; triggers an immediate server-side polling run across international feeds.")
    doc.draw_bullet("Events View:", "Consolidated single-incident news reports grouped across languages and translated into English.")
    doc.draw_bullet("Situations View:", "Multi-day developing storylines tracking how crises unfold over days or weeks.")
    doc.draw_bullet("World View:", "Macro view displaying country mention rankings, top global stories, and national media cards.")
    doc.draw_bullet("Surge Alerts:", "Subscribes your user account to notification bell alerts when a situation's coverage velocity doubles.")

    doc.draw_h2("3.2 Situations View & Geolocation Overrides")
    doc.draw_paragraph(
        "The Situations view uses a split-pane layout. Selecting a situation in the left pane reveals its comprehensive timeline in the right pane, featuring specialized controls:"
    )
    doc.draw_bullet("Watch Situation:", "Pins the story to your notification bell for real-time alerts as new events break.")
    doc.draw_bullet("Show on Overwatch:", "Launches Overwatch, activates the World News layer, flies the camera to the story, and opens its panel.")
    doc.draw_bullet("Wrong place? Drawer:", "Allows analysts to override automated NLP geolocation. You can select the correct Country, specify a City, click 'Place here', or click 'Keep off the globe' to suppress spatial plotting.")

    doc.draw_h2("3.3 The Story Analysis Document (WorldNewsStoryView)")
    doc.draw_paragraph(
        "Clicking 'Read document' on any event opens SentinelGPT's intelligence dossier, organized into three rigorous sections:"
    )
    doc.draw_bullet("1. The Story:", "Articulates 'The Claim' and provides a multi-paragraph synthesis of verified facts.")
    doc.draw_bullet("2. Agreement & Figures:", "Lists 'Agreed Facts' with outlet consensus badges, compares quantitative claims (casualties, financial impact) across outlets, and flags discrepancies.")
    doc.draw_bullet("3. Coverage Breakdown:", "Breaks down reporting by Region (Western vs Non-Western) and Language, highlights State Media badges, provides a full cross-outlet comparison matrix, and offers a publication-ready PDF download.")

    doc.draw_h1("4. Recommended Operational Workflow")
    doc.draw_paragraph(
        "To achieve maximum situational awareness, operators should link geospatial reconnaissance with analytical media investigation following this standard operating procedure:"
    )

    workflow_diagram = [
        "+-----------------------------------------------------------------------------------+",
        "| STEP 1: MACRO SCANNING (Overwatch /admin/osint)                                   |",
        "| Enable LAYERS > WORLD NEWS -> Scan for pulsing purple 'SURGING' markers           |",
        "+-----------------------------------------------------------------------------------+",
        "                                          |",
        "                                          v",
        "+-----------------------------------------------------------------------------------+",
        "| STEP 2: SENSOR & CAMERA CORRELATION (Overwatch Modal)                             |",
        "| Check 'BACKED BY' sensors -> Click 'Nearby Cameras' to inspect ground truth       |",
        "+-----------------------------------------------------------------------------------+",
        "                                          |",
        "                                          v",
        "+-----------------------------------------------------------------------------------+",
        "| STEP 3: AUTOMATED EVIDENCE RECORDING (Overwatch Modal)                            |",
        "| Click [CAPTURE NEARBY CAMERAS] to record cameras within 10 mi for 1 hour          |",
        "+-----------------------------------------------------------------------------------+",
        "                                          |",
        "                                          v",
        "+-----------------------------------------------------------------------------------+",
        "| STEP 4: DEEP ANALYTICAL INVESTIGATION (World News /admin/world-news)              |",
        "| Click [OPEN IN WORLD NEWS] -> Review 'Agreed Facts' -> Filter State Media outlets |",
        "+-----------------------------------------------------------------------------------+",
        "                                          |",
        "                                          v",
        "+-----------------------------------------------------------------------------------+",
        "| STEP 5: CONTINUOUS SURVEILLANCE & RE-ENTRY                                        |",
        "| Click [Watch situation] for bell alerts -> Use [Show on Overwatch] for 3D context |",
        "+-----------------------------------------------------------------------------------+"
    ]
    doc.draw_ascii_diagram(workflow_diagram)

    doc.draw_paragraph(
        "1. Scan the Globe: Maintain Overwatch on a secondary monitor with World News, Radar, and Incidents active. Pulsing SURGING pins identify breaking crises before local reports settle."
    )
    doc.draw_paragraph(
        "2. Ground Sensor Verification: When a pin is selected, examine the BACKED BY section to verify whether physical sensors (USGS quakes, NOAA flood gauges, ODIN power outages) support or contradict the press claims."
    )
    doc.draw_paragraph(
        "3. Live Camera Ground Truth: Open nearby traffic cameras to observe physical road, weather, and crowd conditions in real time."
    )
    doc.draw_paragraph(
        "4. Automated Capture Archive: Click CAPTURE NEARBY CAMERAS to start automated surveillance recording. Captures generate standalone PDF incident reports under the AREAS tab."
    )
    doc.draw_paragraph(
        "5. Deep Investigation & Alerting: Click OPEN IN WORLD NEWS, subscribe via 'Watch situation', and inspect SentinelGPT's agreement matrix to dissect media bias and state-sponsored narratives."
    )

    doc.build_pdf()

if __name__ == "__main__":
    build_guide()
