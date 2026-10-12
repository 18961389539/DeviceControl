"""V0.6 offline studio E2E (browser simulation, not .NET)."""
import asyncio,shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def check():
  async with async_playwright() as pw:
    browser=await pw.chromium.launch(executable_path=shutil.which('chromium') or None,headless=True,args=['--no-sandbox'])
    page=await browser.new_page(viewport={'width':1660,'height':1050},device_scale_factor=1)
    errors=[]
    page.on('pageerror',lambda ex:errors.append(str(ex)))
    await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V0.8_Preview.html').read_text(encoding='utf8'),wait_until='load')
    await page.locator('#loginForm button[type="submit"]').click()
    await page.locator('#loginOverlay').wait_for(state='hidden')
    await page.locator('[data-tab="workflow"]').click()
    await page.wait_for_selector('.studio-node')
    assert await page.locator('.studio-node').count()==9
    assert await page.locator('.studio-branch').count()==4
    assert (await page.locator('#studioActiveVersion').inner_text()).startswith('flow-')
    await page.screenshot(path=str(ROOT/'DeviceControl_V0.8_Regression_Studio06.png'),full_page=True)
    await page.locator('[data-editor-add="Condition"]').click()
    assert await page.locator('.studio-node').count()==10
    assert await page.locator('.studio-branch').count()==6
    await page.locator('.studio-node-body').last.click()
    await page.locator('[data-editor-input="label"]').fill('模拟分流检查')
    await page.locator('[data-editor-input="label"]').press('Tab')
    assert '模拟分流检查' in await page.locator('#studioCanvas').inner_text()
    await page.locator('[data-editor-action="save"]').click()
    await page.wait_for_function("document.querySelectorAll('.studio-version').length >= 2")
    assert 'Draft' in await page.locator('.studio-version').first.inner_text()
    assert await page.locator('[data-editor-action="validate"]').is_enabled()
    await page.locator('[data-editor-action="validate"]').click()
    assert 'Validated' in await page.locator('.studio-version').first.inner_text()
    # Engineer cannot publish.
    assert not await page.locator('[data-editor-action="publish"]').is_enabled()
    await page.locator('#logoutButton').click()
    await page.locator('#loginUser').select_option('admin')
    await page.locator('#loginForm button[type="submit"]').click()
    await page.locator('#loginOverlay').wait_for(state='hidden')
    await page.locator('[data-tab="workflow"]').click()
    await page.wait_for_selector('.studio-version')
    await page.locator('.studio-version').first.click()
    assert await page.locator('[data-editor-action="publish"]').is_enabled()
    page.once('dialog', lambda dialog:asyncio.create_task(dialog.accept()))
    await page.locator('[data-editor-action="publish"]').click()
    await page.wait_for_function("document.querySelector('#studioActiveVersion').textContent === document.querySelector('.studio-version.selected b')?.textContent")
    await page.locator('[data-tab="sequence"]').click()
    assert await page.locator('.flow-node').count()==10
    assert await page.locator('.flow-child').count()==6
    assert not errors,errors
    await browser.close()
    print('PASS V0.6 browser: initial graph, add condition, edit, draft, validation, role check, publish, Runtime graph refresh')
if __name__=='__main__':asyncio.run(check())
