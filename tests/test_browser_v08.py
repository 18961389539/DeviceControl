"""V0.8 browser quality tab E2E (offline simulated runtime)."""
import asyncio,shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def check():
  async with async_playwright() as pw:
    browser=await pw.chromium.launch(executable_path=shutil.which('chromium') or None,headless=True,args=['--no-sandbox'])
    page=await browser.new_page(viewport={'width':1560,'height':980},device_scale_factor=1,accept_downloads=True)
    errors=[]
    page.on('pageerror',lambda e:errors.append(str(e)))
    await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V0.8_Preview.html').read_text(encoding='utf8'),wait_until='load')
    await page.locator('#loginForm button[type="submit"]').click()
    await page.locator('#loginOverlay').wait_for(state='hidden')
    await page.locator('[data-tab="quality"]').click()
    await page.locator('[data-quality-action="run"]').click()
    assert '门禁通过' in await page.locator('#qualityStatus').inner_text(),await page.locator('#qualityProblems').inner_text()
    assert '4 个' == (await page.locator('#qualityTotal').inner_text()).strip()
    assert await page.locator('.quality-case.pass').count()==4
    await page.locator('#qualityCaseName').fill('缺料场景复查')
    await page.locator('#qualityCaseSignal').select_option('PartPresent')
    await page.locator('#qualityCaseState').select_option('false')
    await page.locator('#qualityCaseExpected').select_option('Rejected')
    await page.locator('[data-quality-action="add"]').click()
    await page.locator('[data-quality-action="run"]').click()
    assert await page.locator('.quality-case.pass').count()==5
    async with page.expect_download() as info:
      await page.locator('[data-quality-action="export"]').click()
    assert (await info.value).suggested_filename=='workflow-quality-report.json'
    await page.screenshot(path=str(ROOT/'DeviceControl_V0.8_Quality.png'),full_page=True)
    # Observe real simulated-cycle performance metrics, distinct from model estimate.
    await page.locator('.navigation [data-tab="manual"]').click()
    for a in ('X','Y'):
      await page.locator('#axisSelect').select_option(a)
      await page.locator('[data-action="servo-on"]').click()
      await page.locator('[data-action="axis-home"]').click()
      await page.wait_for_function("document.querySelector('#homedReadout').textContent.includes('已回零')",timeout=15000)
    await page.locator('[data-tab="dashboard"]').click()
    await page.locator('[data-mode="Auto"]').click()
    await page.locator('[data-action="cycle-start"]').click()
    await page.wait_for_function("document.querySelector('#cyclesValue').textContent === '1'",timeout=45000)
    await page.locator('[data-tab="quality"]').click()
    await page.wait_for_function("document.querySelector('#qualityCycleCount').textContent.includes('1 条')",timeout=10000)
    assert await page.locator('#qualityP50').inner_text()!='—'
    assert await page.locator('.quality-bar-row').count()>0
    await page.screenshot(path=str(ROOT/'DeviceControl_V0.8_Performance.png'),full_page=True)
    assert not errors,errors
    await browser.close()
    print('PASS V0.8 browser: scenarios, custom case, JSON report, real simulation cycle metrics')
if __name__=='__main__':asyncio.run(check())
