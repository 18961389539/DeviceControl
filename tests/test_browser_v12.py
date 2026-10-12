"""V1.2 offline browser: both cycles run concurrently, resource panel is functional."""
import asyncio,shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def run():
 async with async_playwright() as pw:
  kw={'headless':True,'args':['--no-sandbox']}
  if shutil.which('chromium'):kw['executable_path']=shutil.which('chromium')
  browser=await pw.chromium.launch(**kw)
  page=await browser.new_page(viewport={'width':1480,'height':1000},device_scale_factor=1)
  errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
  await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V1.2_Preview.html').read_text(encoding='utf8'),wait_until='load')
  await page.locator('#loginForm button[type=submit]').click()
  await page.locator('#loginOverlay').wait_for(state='hidden')
  await page.locator('.navigation [data-tab=stations]').click()
  await page.wait_for_function("document.querySelectorAll('[data-fleet-station]').length===2")
  a=page.locator('[data-fleet-station="A"]');b=page.locator('[data-fleet-station="B"]')
  assert await page.locator('#fleetResourceRows').is_visible()
  for station in (a,b):
   await station.locator('[data-fleet-action=prepare]').click()
   await page.wait_for_function("document.querySelector('[data-fleet-station=\"%s\"] [data-fleet-action=auto]') && !document.querySelector('[data-fleet-station=\"%s\"] [data-fleet-action=auto]').disabled" % (("A","A") if station==a else ("B","B")),timeout=18000)
   await station.locator('[data-fleet-action=auto]').click()
  await page.wait_for_function("!document.querySelector('[data-fleet-station=A] [data-fleet-action=start]').disabled")
  await a.locator('[data-fleet-action=start]').click()
  await page.wait_for_function("!document.querySelector('[data-fleet-station=B] [data-fleet-action=start]').disabled",timeout=5000)
  await b.locator('[data-fleet-action=start]').click()
  await page.wait_for_function("document.getElementById('fleetRunning').textContent==='2'",timeout=10000)
  assert '已分配' in await page.locator('#fleetResourceRows').inner_text()
  assert 'FIFO' in await page.locator('#panel-stations').inner_text()
  await page.screenshot(path=str(ROOT/'DeviceControl_V1.2_Stations.png'),full_page=True)
  await b.locator('[data-fleet-action=stop]').click()
  await a.locator('[data-fleet-action=stop]').click()
  await page.wait_for_function("document.getElementById('fleetRunning').textContent==='0'",timeout=15000)
  await page.locator('.navigation [data-tab=quality]').click()
  await page.locator('[data-quality-action=contract]').click()
  assert '4 / 4' in await page.locator('#qualityContractStatus').inner_text()
  assert not errors, errors
  await browser.close()
  print('PASS V1.2 Chromium: A/B concurrent cycles, FIFO resource panel, stop, quality 4/4')
if __name__=='__main__':asyncio.run(run())
