import { createRequire } from 'node:module';
import { readFile, mkdir, copyFile, rm } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { randomUUID } from 'node:crypto';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(repositoryRoot, 'tests', 'blackbox-e2e', 'package.json'));
const { chromium } = require('playwright');

const baseUrl = new URL(process.env.BLACKBOX_API_URL ?? 'http://127.0.0.1:5000');
const swaggerUrl = new URL('/swagger/index.html', baseUrl).toString();
const mediaDirectory = join(repositoryRoot, 'docs', 'media');
const screenshotsDirectory = join(mediaDirectory, 'blackbox-demo-steps');
const videoDirectory = join(tmpdir(), `blackbox-demo-${randomUUID()}`);
const videoOutput = join(mediaDirectory, 'blackbox-workflow-demo.webm');
const posterOutput = join(mediaDirectory, 'blackbox-workflow-demo.png');
const defaultBravePath = 'C:\\Program Files\\BraveSoftware\\Brave-Browser\\Application\\brave.exe';
const browserPath = process.env.BLACKBOX_BRAVE_PATH
  ?? (existsSync(defaultBravePath) ? defaultBravePath : undefined);

const stepScreenshots = [
  '01-create-endpoint.png',
  '02-create-request.png',
  '03-created-200.png',
  '04-readback-200.png',
  '05-reserve-request.png',
  '06-reservation-active.png',
  '07-complete-request.png',
  '08-completed-200.png',
];

function parseEnvValue(text, key) {
  const line = text.split(/\r?\n/).find((entry) => entry.trimStart().startsWith(`${key}=`));
  if (!line) return undefined;

  let value = line.slice(line.indexOf('=') + 1).trim();
  if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) {
    value = value.slice(1, -1);
  }
  return value;
}

async function getApiKey() {
  if (process.env.BLACKBOX_API_KEY) return process.env.BLACKBOX_API_KEY;
  const envText = await readFile(join(repositoryRoot, '.env'), 'utf8');
  return parseEnvValue(envText, 'BLACKBOX_API_KEY');
}

function requireSuccess(condition, message) {
  if (!condition) throw new Error(message);
}

const apiKey = await getApiKey();
requireSuccess(apiKey && Buffer.byteLength(apiKey, 'utf8') >= 32,
  'BLACKBOX_API_KEY is missing or shorter than 32 UTF-8 bytes.');

const readiness = await fetch(new URL('/health/ready', baseUrl));
requireSuccess(readiness.ok, `Blackbox readiness check failed (${readiness.status}). Start the local API and database first.`);

await mkdir(mediaDirectory, { recursive: true });
await mkdir(screenshotsDirectory, { recursive: true });
await mkdir(videoDirectory, { recursive: true });

const browser = await chromium.launch({
  headless: true,
  ...(browserPath ? { executablePath: browserPath } : {}),
});

let context;
try {
  context = await browser.newContext({
    viewport: { width: 1440, height: 960 },
    deviceScaleFactor: 1,
    recordVideo: { dir: videoDirectory, size: { width: 1440, height: 960 } },
  });

  // Keep the credential in Node and attach it only to protected API requests.
  await context.route(new URL('/api/v1/**', baseUrl).toString(), async (route) => {
    const headers = { ...route.request().headers(), 'x-api-key': apiKey };
    await route.continue({ headers });
  });

  const page = await context.newPage();
  page.setDefaultTimeout(15_000);
  await page.goto(swaggerUrl, { waitUntil: 'domcontentloaded' });
  await page.locator('.opblock').first().waitFor({ state: 'visible' });

  await page.addStyleTag({ content: `
    @keyframes bb-cursor-pulse { 0% { transform: scale(1); opacity: .95; } 100% { transform: scale(3.2); opacity: 0; } }
    @keyframes bb-target-pulse { 0%, 100% { opacity: .9; } 50% { opacity: .45; } }
    #bb-demo-note {
      position: fixed; top: 14px; right: 18px; z-index: 2147483646;
      max-width: 430px; padding: 9px 13px; border-radius: 7px;
      border: 1px solid #526782; background: #172333; color: #f3f7fc;
      box-shadow: 0 4px 14px #0005; font: 12px/1.45 system-ui, sans-serif;
      pointer-events: none;
    }
    #bb-demo-cursor {
      position: fixed; left: 0; top: 0; width: 22px; height: 22px; z-index: 2147483647;
      border: 3px solid #fff; border-radius: 50%; background: #00c8ff99;
      box-shadow: 0 0 0 3px #087e9c, 0 2px 10px #000b;
      transform: translate(-50%, -50%); pointer-events: none;
    }
    #bb-demo-cursor.bb-click::after {
      content: ''; position: absolute; inset: -4px; border: 3px solid #ff524f; border-radius: 50%;
      animation: bb-cursor-pulse .7s ease-out forwards;
    }
    #bb-demo-spotlight {
      position: fixed; inset: 0; z-index: 2147483640; display: none; pointer-events: none;
      background: radial-gradient(circle at var(--bb-x, 50%) var(--bb-y, 50%), transparent 0 68px, #07101c99 112px);
    }
    #bb-demo-annotations { position: fixed; inset: 0; z-index: 2147483645; pointer-events: none; }
    #bb-demo-step-card {
      position: absolute; width: 365px; box-sizing: border-box;
      padding: 13px 16px 14px; border: 1px solid #83a5c8; border-left: 5px solid #32c9f2;
      border-radius: 8px; color: #eef6ff; background: #142131ed; box-shadow: 0 5px 18px #0008;
      font: 15px/1.35 system-ui, sans-serif;
    }
    #bb-demo-step-card strong { display: block; margin-bottom: 4px; color: #5ddcf7; font-size: 12px; letter-spacing: .08em; }
    #bb-demo-step-card span { display: block; font-size: 16px; font-weight: 650; }
    #bb-demo-step-card small { display: block; margin-top: 4px; color: #c1d0df; font-size: 12px; }
    #bb-demo-arrow { stroke: #ff5959; stroke-width: 5; fill: none; filter: drop-shadow(0 1px 3px #000d); }
    #bb-demo-target-ring { stroke: #ff5959; stroke-width: 4; fill: #ff595922; animation: bb-target-pulse 1.1s ease-in-out infinite; }
    #bb-demo-spotlight-toast {
      position: fixed; left: 22px; bottom: 20px; z-index: 2147483646; display: none;
      padding: 8px 12px; border-radius: 6px; background: #142131ed; color: white;
      border: 1px solid #83a5c8; box-shadow: 0 4px 14px #0007; font: 13px system-ui, sans-serif;
      pointer-events: none;
    }
  ` });

  await page.evaluate(() => {
    const note = document.createElement('aside');
    note.id = 'bb-demo-note';
    note.textContent = 'Local demo · API key is supplied by the recorder and omitted from the visible request';
    document.body.append(note);

    const cursor = document.createElement('div');
    cursor.id = 'bb-demo-cursor';
    document.body.append(cursor);

    const spotlight = document.createElement('div');
    spotlight.id = 'bb-demo-spotlight';
    document.body.append(spotlight);

    const toast = document.createElement('div');
    toast.id = 'bb-demo-spotlight-toast';
    toast.textContent = 'Pointer spotlight: Ctrl twice to toggle';
    document.body.append(toast);

    window.__blackboxSpotlight = false;
    let previousControlTap = 0;
    document.addEventListener('keydown', (event) => {
      if (event.key !== 'Control' || event.repeat) return;
      const now = performance.now();
      if (now - previousControlTap < 700) {
        window.__blackboxSpotlight = !window.__blackboxSpotlight;
        spotlight.style.display = window.__blackboxSpotlight ? 'block' : 'none';
        toast.textContent = `Pointer spotlight ${window.__blackboxSpotlight ? 'ON' : 'OFF'} · Ctrl twice to toggle`;
        toast.style.display = 'block';
        window.setTimeout(() => { toast.style.display = 'none'; }, 1500);
      }
      previousControlTap = now;
    }, true);

    window.addEventListener('pointermove', (event) => {
      cursor.style.left = `${event.clientX}px`;
      cursor.style.top = `${event.clientY}px`;
      spotlight.style.setProperty('--bb-x', `${event.clientX}px`);
      spotlight.style.setProperty('--bb-y', `${event.clientY}px`);
    }, true);

    window.addEventListener('click', () => {
      cursor.classList.remove('bb-click');
      void cursor.offsetWidth;
      cursor.classList.add('bb-click');
    }, true);
  });

  await page.waitForTimeout(1_800);

  async function toggleSpotlight() {
    await page.keyboard.press('Control');
    await page.waitForTimeout(180);
    await page.keyboard.press('Control');
    await page.waitForTimeout(850);
  }

  async function findOperation(method, path) {
    const summaryPath = page.locator(`.opblock-summary-path[data-path="${path}"]`);
    const operation = summaryPath.locator('xpath=ancestor::div[contains(concat(" ", normalize-space(@class), " "), " opblock ")]');
    await operation.waitFor({ state: 'visible' });
    const actualMethod = (await operation.locator('.opblock-summary-method').innerText()).trim().toUpperCase();
    requireSuccess(actualMethod === method, `Expected ${method} for ${path}, received ${actualMethod}.`);
    return operation;
  }

  async function showAnnotation(target, { step, title, detail, screenshot }) {
    await target.scrollIntoViewIfNeeded();
    const box = await target.boundingBox();
    requireSuccess(box, `Could not locate the screen target for step ${step}.`);
    const center = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
    await page.mouse.move(center.x, center.y, { steps: 28 });
    await page.waitForTimeout(900);

    await page.evaluate(({ box, step, title, detail }) => {
      document.getElementById('bb-demo-annotations')?.remove();
      const layer = document.createElement('div');
      layer.id = 'bb-demo-annotations';

      const card = document.createElement('div');
      card.id = 'bb-demo-step-card';
      const label = document.createElement('strong');
      label.textContent = `STEP ${step}`;
      const heading = document.createElement('span');
      heading.textContent = title;
      const description = document.createElement('small');
      description.textContent = detail;
      card.append(label, heading, description);
      layer.append(card);

      const width = window.innerWidth;
      const height = window.innerHeight;
      const targetX = box.x + box.width / 2;
      const targetY = box.y + box.height / 2;
      const cardWidth = 365;
      const cardLeft = targetX < width - cardWidth - 36 ? width - cardWidth - 22 : 22;
      const cardTop = targetY < 190 ? height - 112 : 76;
      card.style.left = `${cardLeft}px`;
      card.style.top = `${cardTop}px`;
      const startX = cardLeft < width / 2 ? cardLeft + cardWidth + 14 : cardLeft - 14;
      const startY = cardTop + 45;
      const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      svg.setAttribute('width', String(width));
      svg.setAttribute('height', String(height));
      svg.style.position = 'absolute';
      svg.style.inset = '0';
      const defs = document.createElementNS(svg.namespaceURI, 'defs');
      const marker = document.createElementNS(svg.namespaceURI, 'marker');
      marker.setAttribute('id', 'bb-demo-arrowhead');
      marker.setAttribute('markerWidth', '12');
      marker.setAttribute('markerHeight', '12');
      marker.setAttribute('refX', '9');
      marker.setAttribute('refY', '6');
      marker.setAttribute('orient', 'auto');
      marker.setAttribute('markerUnits', 'strokeWidth');
      const arrowHead = document.createElementNS(svg.namespaceURI, 'path');
      arrowHead.setAttribute('d', 'M 0 0 L 12 6 L 0 12 z');
      arrowHead.setAttribute('fill', '#ff5959');
      marker.append(arrowHead);
      defs.append(marker);
      svg.append(defs);

      const line = document.createElementNS(svg.namespaceURI, 'line');
      line.id = 'bb-demo-arrow';
      line.setAttribute('x1', String(startX));
      line.setAttribute('y1', String(startY));
      line.setAttribute('x2', String(targetX));
      line.setAttribute('y2', String(targetY));
      line.setAttribute('marker-end', 'url(#bb-demo-arrowhead)');
      svg.append(line);

      const ring = document.createElementNS(svg.namespaceURI, 'ellipse');
      ring.id = 'bb-demo-target-ring';
      ring.setAttribute('cx', String(targetX));
      ring.setAttribute('cy', String(targetY));
      ring.setAttribute('rx', String(Math.min(72, Math.max(36, Math.min(box.width, box.height) / 2 + 16))));
      ring.setAttribute('ry', String(Math.min(72, Math.max(25, box.height / 2 + 12))));
      svg.append(ring);
      layer.append(svg);
      document.body.append(layer);
    }, { box, step, title, detail });

    await page.waitForTimeout(1_800);
    if (screenshot) {
      await page.screenshot({ path: join(screenshotsDirectory, screenshot), fullPage: false });
    }
    return center;
  }

  async function clickWithGuidance(target, details) {
    const center = await showAnnotation(target, details);
    await page.waitForTimeout(1_200);
    await page.mouse.click(center.x, center.y);
    await page.waitForTimeout(850);
    await page.evaluate(() => document.getElementById('bb-demo-annotations')?.remove());
  }

  async function enterText(target, value, guidance) {
    await target.scrollIntoViewIfNeeded();
    const { screenshot, ...annotation } = guidance ?? {};
    const center = guidance
      ? await showAnnotation(target, annotation)
      : await (async () => {
        const box = await target.boundingBox();
        requireSuccess(box, 'Could not locate a Swagger input field.');
        const point = { x: box.x + Math.min(40, box.width / 2), y: box.y + Math.min(25, box.height / 2) };
        await page.mouse.move(point.x, point.y, { steps: 20 });
        await page.waitForTimeout(700);
        return point;
      })();
    await page.waitForTimeout(800);
    await page.mouse.click(center.x, center.y);
    await target.fill('');
    await target.pressSequentially(value, { delay: 11 });
    await page.waitForTimeout(650);
    if (screenshot) await showAnnotation(target, { ...annotation, screenshot });
    await page.evaluate(() => document.getElementById('bb-demo-annotations')?.remove());
  }

  async function executeAndRead(operation, method, path, guidance) {
    const responsePromise = page.waitForResponse((response) => {
      const requestUrl = new URL(response.url());
      return response.request().method() === method && requestUrl.pathname.startsWith(path);
    });
    const button = operation.locator('button.execute');
    const center = await showAnnotation(button, guidance);
    await page.waitForTimeout(1_200);
    await page.mouse.click(center.x, center.y);
    const response = await responsePromise;
    requireSuccess(response.status() === 200,
      `${method} ${path} returned HTTP ${response.status()}, expected 200.`);
    await operation.locator('.responses-inner .response-col_status').last().waitFor({ state: 'visible' });
    const body = await response.json();
    await page.waitForTimeout(950);
    await page.evaluate(() => document.getElementById('bb-demo-annotations')?.remove());
    return body;
  }

  async function recordResponse(operation, { step, title, detail, screenshot }) {
    const responseBody = operation.locator('.responses-inner pre').filter({ hasText: '"status"' }).last();
    await responseBody.waitFor({ state: 'visible' });
    await showAnnotation(responseBody, { step, title, detail, screenshot });
    await page.waitForTimeout(2_400);
    await page.evaluate(() => document.getElementById('bb-demo-annotations')?.remove());
  }

  await page.keyboard.press('Control');
  await page.waitForTimeout(180);
  await page.keyboard.press('Control');
  await page.waitForTimeout(1_200);

  const runId = `PORTFOLIO-DEMO-${new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14)}-${randomUUID().slice(0, 8)}`;
  const orderId = `ORD-${randomUUID().slice(0, 8).toUpperCase()}`;
  const create = await findOperation('POST', '/api/v1/workflows');

  await clickWithGuidance(create.locator('.opblock-summary'), {
    step: '01 / 08',
    title: 'Open workflow creation',
    detail: 'Expand POST /api/v1/workflows to inspect the operation.',
    screenshot: '01-create-endpoint.png',
  });
  await page.keyboard.press('Control');
  await page.waitForTimeout(180);
  await page.keyboard.press('Control');
  await page.waitForTimeout(600);

  await clickWithGuidance(create.locator('button.try-out__btn'), {
    step: '02 / 08',
    title: 'Enable the request form',
    detail: 'Try it out exposes the editable JSON body.',
  });

  const createBody = create.locator('textarea.body-param__text');
  const createRequest = JSON.stringify({
    correlationId: runId,
    payloadJson: JSON.stringify({ orderId, customer: 'Portfolio Demo', amount: 129.5 }),
  }, null, 2);
  await enterText(createBody, createRequest, {
    step: '02 / 08',
    title: 'Enter a valid workflow payload',
    detail: 'The payload itself must be serialized JSON.',
    screenshot: '02-create-request.png',
  });
  const created = await executeAndRead(create, 'POST', '/api/v1/workflows', {
    step: '03 / 08',
    title: 'Submit the workflow request',
    detail: 'The API validates JSON and stores workflow state with its Outbox event.',
  });
  requireSuccess(created.id && created.status === 'Created', 'Workflow creation did not return a Created workflow.');
  await recordResponse(create, {
    step: '03 / 08',
    title: 'Confirm workflow creation',
    detail: 'HTTP 200 · status Created · version 1',
    screenshot: '03-created-200.png',
  });

  const read = await findOperation('GET', '/api/v1/workflows/{id}');
  await clickWithGuidance(read.locator('.opblock-summary'), {
    step: '05 / 08',
    title: 'Read the stored workflow',
    detail: 'Fetch the workflow using the ID returned by the create operation.',
  });
  await clickWithGuidance(read.locator('button.try-out__btn'), {
    step: '05 / 08',
    title: 'Enter the workflow ID',
    detail: 'The detail endpoint reads the persisted record by its identifier.',
  });
  await enterText(read.locator('.parameters-container input').first(), created.id, {
    step: '05 / 08',
    title: 'Use the returned workflow ID',
    detail: 'Read the persisted workflow rather than relying only on the create response.',
  });
  await toggleSpotlight();
  const fetched = await executeAndRead(read, 'GET', '/api/v1/workflows/', {
    step: '05 / 08',
    title: 'Fetch the saved workflow',
    detail: 'Read the same workflow ID returned from the create operation.',
  });
  requireSuccess(fetched.id === created.id && fetched.status === 'Created', 'Workflow read-back did not match the created record.');
  await toggleSpotlight();
  await recordResponse(read, {
    step: '04 / 08',
    title: 'Verify the persisted state',
    detail: 'The same ID is returned with status Created.',
    screenshot: '04-readback-200.png',
  });

  const reserve = await findOperation('POST', '/api/v1/workflows/{id}/reserve');
  await clickWithGuidance(reserve.locator('.opblock-summary'), {
    step: '07 / 08',
    title: 'Reserve inventory',
    detail: 'Use the workflow ID and request one SKU-DESK-003 unit.',
  });
  await clickWithGuidance(reserve.locator('button.try-out__btn'), {
    step: '07 / 08',
    title: 'Prepare the reservation request',
    detail: 'The API will apply this reservation to the workflow.',
  });
  await enterText(reserve.locator('.parameters-container input').first(), created.id, {
    step: '07 / 08',
    title: 'Select the workflow to reserve for',
    detail: 'Use the same workflow ID returned earlier.',
  });
  await enterText(reserve.locator('textarea.body-param__text'), JSON.stringify({ resourceId: 'SKU-DESK-003', quantity: 1 }, null, 2), {
    step: '05 / 08',
    title: 'Choose an inventory item',
    detail: 'Reserve one unit of SKU-DESK-003.',
    screenshot: '05-reserve-request.png',
  });
  const reservation = await executeAndRead(reserve, 'POST', '/api/v1/workflows/', {
    step: '05 / 08',
    title: 'Submit the reservation',
    detail: 'PostgreSQL protects the stock reservation while the workflow advances.',
  });
  requireSuccess(reservation.status === 'Active' && reservation.workflowId === created.id,
    'Reservation response did not match the workflow.');
  await recordResponse(reserve, {
    step: '06 / 08',
    title: 'Confirm inventory reservation',
    detail: 'HTTP 200 · reservation status Active · quantity 1',
    screenshot: '06-reservation-active.png',
  });

  const complete = await findOperation('POST', '/api/v1/workflows/{id}/complete');
  await clickWithGuidance(complete.locator('.opblock-summary'), {
    step: '07 / 08',
    title: 'Complete the workflow',
    detail: 'Advance the workflow after the inventory reservation succeeds.',
  });
  await clickWithGuidance(complete.locator('button.try-out__btn'), {
    step: '07 / 08',
    title: 'Confirm the completion action',
    detail: 'The endpoint uses the same workflow ID for its state transition.',
  });
  await enterText(complete.locator('.parameters-container input').first(), created.id, {
    step: '07 / 08',
    title: 'Confirm the workflow ID',
    detail: 'Apply the completion transition to the workflow just reserved.',
  });
  const completed = await executeAndRead(complete, 'POST', '/api/v1/workflows/', {
    step: '07 / 08',
    title: 'Submit workflow completion',
    detail: 'The response confirms the final Completed state.',
    screenshot: '07-complete-request.png',
  });
  requireSuccess(completed.status === 'Completed', 'Workflow did not reach Completed state.');
  await recordResponse(complete, {
    step: '08 / 08',
    title: 'Verify workflow completion',
    detail: 'HTTP 200 · status Completed',
    screenshot: '08-completed-200.png',
  });

  await page.evaluate(() => document.getElementById('bb-demo-annotations')?.remove());
  await page.waitForTimeout(1_400);
  await page.screenshot({ path: posterOutput, fullPage: false });
  const video = page.video();
  const temporaryVideoPath = await video.path();
  await context.close();
  context = undefined;
  await copyFile(temporaryVideoPath, videoOutput);

  console.log('Blackbox walkthrough recorded successfully.');
  console.log('Verified browser flow: Created -> persisted read-back -> reservation Active -> Completed.');
  console.log(`Video (${stepScreenshots.length} annotated screenshots): ${videoOutput}`);
  console.log(`Step images: ${screenshotsDirectory}`);
  console.log(`Poster: ${posterOutput}`);
} finally {
  if (context) await context.close();
  await browser.close();
  await rm(videoDirectory, { recursive: true, force: true });
}
