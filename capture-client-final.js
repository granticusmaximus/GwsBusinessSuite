const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const BASE = 'http://localhost:5214';
const SHOTS = path.join(__dirname, 'shots');
const MAIL = path.join(__dirname, 'mail-pickup');
const CONTACT_EMAIL = 'jamie.docstemp@example.com';

function latestEmlContaining(fragment) {
  const files = fs.readdirSync(MAIL)
    .filter(f => f.endsWith('.eml'))
    .map(f => ({ f, t: fs.statSync(path.join(MAIL, f)).mtimeMs }))
    .sort((a, b) => b.t - a.t);
  for (const { f } of files) {
    const content = fs.readFileSync(path.join(MAIL, f), 'utf8');
    if (content.includes(fragment)) return content;
  }
  throw new Error('No .eml found containing: ' + fragment);
}
function extractLoginUrl(emlContent) {
  const match = emlContent.match(/https?:\/\/[^\s<>"]+client-portal\/auth\/consume[^\s<>"]+/);
  if (!match) throw new Error('No login link found in email');
  return match[0].replace(/=$/, '').replace(/=\r?\n/g, '').replace(/&gt;$/, '');
}
async function shot(page, name) {
  await page.screenshot({ path: path.join(SHOTS, name), fullPage: true });
  console.log('captured', name);
}

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const client = await ctx.newPage();

  await client.goto(`${BASE}/client-portal/login`);
  await client.fill('input[name="email"]', CONTACT_EMAIL);
  await client.click('button[type="submit"]');
  await client.waitForTimeout(1500);

  const eml = latestEmlContaining(CONTACT_EMAIL);
  const loginUrl = extractLoginUrl(eml);
  await client.goto(loginUrl);
  await client.waitForTimeout(1500);
  console.log('post-consume URL:', client.url());

  await client.goto(`${BASE}/client-portal/support`);
  await client.waitForSelector('text=Your Tickets', { timeout: 15000 });
  await client.waitForTimeout(4000);
  console.log('support page URL:', client.url());

  // Diagnostic first: does ANY interactive element on this page reach the server at all?
  // Submit the New Ticket form with real content - if this silently no-ops too, the whole
  // page's interactivity is broken, not just the ticket-list button.
  await client.fill('input[placeholder="Subject"]', 'Question about my last invoice');
  await client.fill('textarea[placeholder="How can we help?"]', "Hi - I noticed a charge on my latest invoice I don't recognize. Could you take a look?");
  await client.waitForTimeout(300);
  const ticketCountBefore = await client.locator('.list-group-item').count();
  console.log('ticket count before New Ticket submit:', ticketCountBefore);
  await client.locator('button:has-text("Send")').first().click();
  await client.waitForTimeout(2000);
  const ticketCountAfter = await client.locator('.list-group-item').count();
  console.log('ticket count after New Ticket submit:', ticketCountAfter);
  await shot(client, 'diag-after-new-ticket-submit.png');

  if (ticketCountAfter > ticketCountBefore) {
    console.log('INTERACTIVITY WORKS - new ticket was created. Now testing existing ticket click...');
  } else {
    console.log('INTERACTIVITY BROKEN PAGE-WIDE - New Ticket submit had no effect either.');
  }

  const ticketButton = client.locator('button.list-group-item', { hasText: "Website contact form isn't sending emails" });
  await ticketButton.click();
  await client.waitForTimeout(2500);
  console.log('after click on existing ticket, textarea count:', await client.locator('textarea[placeholder="Reply..."]').count());
  await shot(client, 'diag-after-existing-ticket-click.png');

  await browser.close();
  console.log('DONE');
})().catch(err => { console.error(err); process.exit(1); });
