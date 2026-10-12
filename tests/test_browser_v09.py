"""V0.9 offline browser contract self-check, quality scenarios and legacy UI smoke."""
import asyncio,shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def run():
 async with async_playwright() as pw:
  browser=await pw.chromium.launch(**({'executable_path':shutil.which('chromium')} if shutil.which('chromium') else {}),headless=True,args=['--no-sandbox'])
  page=await browser.new_page(viewport={'width':1460,'height':920},device_scale_factor=1)
  errors=[]
  page.on('pageerror',lambda e:errors.append(str(e)))
  await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V0.9_Preview.html').read_text(),wait_until='load')
  await page.locator('#loginForm button[type="submit"]').click()
  await page.locator('#loginOverlay').wait_for(state='hidden')
  await page.locator('.navigation [data-tab="quality"]').click()
  await page.locator('[data-quality-action="contract"]').click()
  assert '4 / 4' in await page.locator('#qualityContractStatus').inner_text()
  assert await page.locator('#qualityContractCases > div').count()==4
  await page.locator('[data-quality-action="run"]').click()
  assert '门禁通过' in await page.locator('#qualityStatus').inner_text()
  assert await page.locator('.quality-case.pass').count()==4
  assert not errors,errors
  await page.wait_for_timeout(4500)  # let the transient toast clear for the preview screenshot
  await page.screenshot(path=str(ROOT/'DeviceControl_V0.9_Quality.png'),full_page=True)
  await page.locator('.navigation [data-tab="workflow"]').click()
  assert await page.locator('#panel-workflow').is_visible()
  await page.locator('.navigation [data-tab="replay"]').click()
  assert await page.locator('#panel-replay').is_visible()
  await browser.close()
  print('PASS V0.9 browser: 4 contract checks, 4 quality scenarios, workflow/replay tabs, no console errors')
if __name__=='__main__': asyncio.run(run())
