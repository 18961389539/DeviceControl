"""V1.3 browser A/B workspace selection, editor drafts, recipes and diagnostics isolation."""
import asyncio,shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def run():
 async with async_playwright() as pw:
  kw={'headless':True,'args':['--no-sandbox']}
  if shutil.which('chromium'):kw['executable_path']=shutil.which('chromium')
  browser=await pw.chromium.launch(**kw)
  page=await browser.new_page(viewport={'width':1520,'height':980},device_scale_factor=1)
  errors=[];page.on('pageerror',lambda e: errors.append(str(e)))
  await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V1.3_Preview.html').read_text(encoding='utf8'),wait_until='load')
  await page.locator('#loginForm button[type=submit]').click()
  await page.locator('#loginOverlay').wait_for(state='hidden',timeout=8000)
  async def tab(name):
   await page.locator(f'.navigation [data-tab="{name}"]').click()
  async def select(id):
   await page.locator('#workspaceStation').select_option(id)
   await page.wait_for_function('id => document.getElementById("workspaceTag").textContent === "工作区 " + id',arg=id,timeout=8000)
  assert await page.locator('#workspaceStation').input_value()=='A'
  await tab('workflow')
  await page.locator('#studioCanvas [data-editor-select]').first.wait_for(state='visible')
  await page.locator('#studioCanvas [data-editor-select]').first.click()
  inp=page.locator('#studioInspector [data-editor-input=label]')
  await inp.fill('A工站自定义节点')
  await inp.dispatch_event('change')
  assert '未保存' in await page.locator('#studioWorkspace').inner_text()
  await select('B')
  await page.wait_for_function('() => document.querySelector("#studioWorkspace").textContent.includes("工站 B")')
  assert 'A工站自定义节点' not in await page.locator('#studioCanvas').inner_text()
  await page.locator('#studioCanvas [data-editor-select]').first.click()
  inp=page.locator('#studioInspector [data-editor-input=label]')
  await inp.fill('B工站自定义节点');await inp.dispatch_event('change')
  await select('A')
  assert 'A工站自定义节点' in await page.locator('#studioCanvas').inner_text()
  assert 'B工站自定义节点' not in await page.locator('#studioCanvas').inner_text()
  await select('B')
  assert 'B工站自定义节点' in await page.locator('#studioCanvas').inner_text()
  # Recipe writes scoped to B, verify A unaffected.
  await tab('governance')
  await page.locator('#recipeVersions .recipe-item').first.wait_for(state='visible')
  await page.locator('#recipeName').fill('仅工站B')
  await page.locator('[data-governance=draft]').click()
  await page.wait_for_function('() => document.querySelectorAll("#recipeVersions .recipe-item").length === 2',timeout=8000)
  assert '仅工站B' in await page.locator('#recipeVersions').inner_text()
  await select('A')
  await page.wait_for_function('() => document.querySelectorAll("#recipeVersions .recipe-item").length === 1',timeout=8000)
  assert '仅工站B' not in await page.locator('#recipeVersions').inner_text()
  await tab('stations')
  await page.locator('#fleetStations [data-fleet-station="B"] [data-fleet-open="B"]').click()
  assert await page.locator('#workspaceStation').input_value()=='B'
  await page.wait_for_function('() => document.querySelector("#panel-dashboard").classList.contains("active")')
  await tab('io')
  await page.locator('#inputRows [data-signal-name=PartPresent]').click()
  await page.wait_for_function('() => document.querySelector("#inputRows [data-signal-name=PartPresent]").dataset.next === "true"',timeout=7000)
  await select('A')
  assert await page.locator('#inputRows [data-signal-name=PartPresent]').get_attribute('data-next')=='false'
  # Produce a completed B cycle, then prove replay never leaks into A.
  await select('B')
  await page.locator('#inputRows [data-signal-name=PartPresent]').click()
  await page.wait_for_function('() => document.querySelector("#inputRows [data-signal-name=PartPresent]").dataset.next === "false"',timeout=7000)
  await tab('stations')
  b=page.locator('[data-fleet-station="B"]')
  await b.locator('[data-fleet-action=prepare]').click()
  await page.wait_for_function('() => !document.querySelector("[data-fleet-station=B] [data-fleet-action=auto]").disabled',timeout=18000)
  await b.locator('[data-fleet-action=auto]').click()
  await page.wait_for_function('() => !document.querySelector("[data-fleet-station=B] [data-fleet-action=start]").disabled',timeout=10000)
  await b.locator('[data-fleet-action=start]').click()
  await page.wait_for_function('() => document.querySelector("[data-fleet-station=B] [data-fleet-action=start]") && !document.querySelector("[data-fleet-station=B] [data-fleet-action=start]").disabled',timeout=20000)
  await tab('replay')
  await page.wait_for_function('() => document.querySelectorAll("#replayRuns [data-replay-run]").length >= 1',timeout=5000)
  assert '无记录' not in await page.locator('#replayMeta').inner_text()
  await select('A')
  await page.wait_for_function('() => document.querySelectorAll("#replayRuns [data-replay-run]").length === 0',timeout=8000)
  await tab('quality')
  await page.locator('[data-quality-action=contract]').click()
  assert '4 / 4' in await page.locator('#qualityContractStatus').inner_text()
  await tab('workflow')
  await page.screenshot(path=str(ROOT/'DeviceControl_V1.3_Workspace.png'),full_page=True)
  assert not errors,errors
  await browser.close()
  print('PASS V1.3 Chromium: workspace selection, unsaved editor buffers, recipe isolation, DI, replay isolation, quality 4/4')
if __name__=='__main__':asyncio.run(run())
