"""V1.1 offline multi-station UI, action routing, shared logical resource and role gating."""
import asyncio,shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def run():
 async with async_playwright() as pw:
  kwargs={'headless':True,'args':['--no-sandbox']}
  if shutil.which('chromium'): kwargs['executable_path']=shutil.which('chromium')
  browser=await pw.chromium.launch(**kwargs)
  page=await browser.new_page(viewport={'width':1440,'height':950})
  errors=[]
  page.on('pageerror',lambda e:errors.append(str(e)))
  await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V1.1_Preview.html').read_text(encoding='utf8'),wait_until='load')
  await page.locator('#loginForm button[type=submit]').click()
  await page.locator('#loginOverlay').wait_for(state='hidden')
  await page.locator('.navigation [data-tab=stations]').click()
  assert await page.locator('#panel-stations').is_visible()
  await page.wait_for_function("document.querySelectorAll('[data-fleet-station]').length===2")
  a=page.locator('[data-fleet-station="A"]');b=page.locator('[data-fleet-station="B"]')
  assert '待机' in await a.inner_text()
  await a.locator('[data-fleet-action=prepare]').click()
  await page.wait_for_function("!document.querySelector('[data-fleet-station=A] [data-fleet-action=prepare]').disabled",timeout=15000)
  # Re-ready B (A's preparation didn't affect it).
  assert await b.locator('[data-fleet-action=prepare]').is_enabled()
  await b.locator('[data-fleet-action=prepare]').click()
  await page.wait_for_function("!document.querySelector('[data-fleet-station=B] [data-fleet-action=prepare]').disabled",timeout=15000)
  await a.locator('[data-fleet-action=auto]').click()
  await page.wait_for_function("document.querySelector('[data-fleet-station=A] [data-fleet-action=start]')&&!document.querySelector('[data-fleet-station=A] [data-fleet-action=start]').disabled")
  await b.locator('[data-fleet-action=auto]').click()
  await page.wait_for_function("!document.querySelector('[data-fleet-station=B] [data-fleet-action=start]').disabled")
  await a.locator('[data-fleet-action=start]').click()
  await page.wait_for_function("document.getElementById('fleetOwner').textContent.includes('A')")
  assert await b.locator('[data-fleet-action=start]').is_disabled(), 'shared resource must block B'
  await a.locator('[data-fleet-action=stop]').click()
  await page.wait_for_function("document.getElementById('fleetOwner').textContent==='空闲'",timeout=15000)
  await page.wait_for_function("!document.querySelector('[data-fleet-station=B] [data-fleet-action=start]').disabled")
  await b.locator('[data-fleet-action=start]').click()
  await page.wait_for_function("document.getElementById('fleetOwner').textContent.includes('B')")
  assert await a.locator('[data-fleet-action=start]').is_disabled()
  await b.locator('[data-fleet-action=stop]').click()
  await page.wait_for_function("document.getElementById('fleetOwner').textContent==='空闲'",timeout=15000)
  assert '待恢复' in await a.inner_text() and '待恢复' in await b.inner_text()
  await page.screenshot(path=str(ROOT/'DeviceControl_V1.1_Stations.png'),full_page=True)
  await page.locator('.navigation [data-tab=quality]').click()
  await page.locator('[data-quality-action=contract]').click()
  assert '4 / 4' in await page.locator('#qualityContractStatus').inner_text()
  assert not errors,errors
  await browser.close()
  print('PASS V1.1 offline Chromium: dual station, preparation, isolation, shared cycle gate, stop, quality 4/4')
if __name__=='__main__':asyncio.run(run())
