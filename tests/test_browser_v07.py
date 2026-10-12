"""V0.7 browser E2E. Simulation only. Requires local Chromium + Playwright."""
import asyncio,shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]

async def check():
  async with async_playwright() as pw:
    browser=await pw.chromium.launch(executable_path=shutil.which('chromium') or None,headless=True,args=['--no-sandbox'])
    page=await browser.new_page(viewport={'width':1690,'height':1000},device_scale_factor=1)
    errors=[]
    page.on('pageerror',lambda ex:errors.append(str(ex)))
    await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V0.8_Preview.html').read_text(encoding='utf8'),wait_until='load')
    await page.locator('#loginForm button[type="submit"]').click()
    await page.locator('#loginOverlay').wait_for(state='hidden')
    await page.locator('[data-tab="workflow"]').click()
    await page.wait_for_selector('.studio-node')
    assert await page.locator('.studio-node').count()==9
    await page.locator('.studio-node-body').first.click()
    await page.locator('[data-editor-input="label"]').fill('V07 测试流程输入')
    await page.locator('[data-editor-input="label"]').press('Tab')
    assert await page.locator('#studioUndo').is_enabled()
    assert '属性变更' in await page.locator('#studioDiff').inner_text() or '修改' in await page.locator('#studioDiff').inner_text()
    await page.locator('#studioUndo').click()
    assert '与当前发布版本无差异' in await page.locator('#studioDiff').inner_text()
    assert await page.locator('#studioRedo').is_enabled()
    await page.locator('#studioRedo').click()
    assert 'V07 测试流程输入' in await page.locator('#studioCanvas').inner_text()
    await page.locator('#studioUndo').click()
    # Nested branch cloning gives fresh IDs and preserves static validation.
    group_id=await page.evaluate("window.STATION_CONFIG.sequence.find(n=>n.type==='Parallel').id")
    await page.locator(f'.studio-node-body[data-editor-select="{group_id}"]').click()
    await page.locator('#studioDuplicate').click()
    assert await page.locator('.studio-node').count()==10
    assert await page.locator('.studio-branch').count()==6
    assert '静态检查通过' in await page.locator('#studioValidation').inner_text()
    await page.screenshot(path=str(ROOT/'DeviceControl_V0.8_Regression_Studio07.png'),full_page=True)
    # Reset any draft and run the original simulated process.
    await page.locator('[data-editor-action="reset"]').click()
    await page.locator('.navigation [data-tab="manual"]').click()
    for axis in ('X','Y'):
      await page.locator('#axisSelect').select_option(axis)
      await page.locator('[data-action="servo-on"]').click()
      await page.locator('[data-action="axis-home"]').click()
      await page.wait_for_function("document.querySelector('#homedReadout').textContent.includes('已回零')",timeout=15000)
    await page.locator('[data-tab="dashboard"]').click()
    await page.locator('[data-mode="Auto"]').click()
    await page.locator('[data-action="cycle-start"]').click()
    await page.wait_for_function("document.querySelector('#cyclesValue').textContent === '1'",timeout=45000)
    await page.locator('[data-tab="replay"]').click()
    await page.wait_for_selector('.replay-event')
    assert await page.locator('.replay-event').count()>=9
    assert await page.locator('.replay-watch-row').count()>=7
    assert 'DI.PartPresent' in await page.locator('#replayWatch').inner_text()
    await page.locator('[data-replay-action="next"]').click()
    assert '2 / ' in await page.locator('#replayIndex').inner_text()
    await page.locator('[data-replay-action="prev"]').click()
    assert '1 / ' in await page.locator('#replayIndex').inner_text()
    await page.locator('[data-replay-action="play"]').click()
    assert '暂停回放' in await page.locator('#replayPlay').inner_text()
    await page.locator('[data-replay-action="play"]').click()
    await page.screenshot(path=str(ROOT/'DeviceControl_V0.8_Regression_Replay.png'),full_page=True)
    assert not errors,errors
    await browser.close()
    print('PASS V0.7 browser: undo/redo, revision diff, duplicate nested nodes, full simulated cycle, recorded variable replay')

if __name__=='__main__':asyncio.run(check())
