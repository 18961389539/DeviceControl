"""Offline V1.5 upgrade preflight, publish, optimistic guard and rollback smoke.
Not evidence of .NET HTTP correctness or durability.
"""
import asyncio, shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def run():
 async with async_playwright() as pw:
  args={'headless':True,'args':['--no-sandbox']}
  if shutil.which('chromium'):args['executable_path']=shutil.which('chromium')
  browser=await pw.chromium.launch(**args)
  page=await browser.new_page(viewport={'width':1510,'height':900})
  errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
  await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V1.5_Preview.html').read_text(encoding='utf8'),wait_until='load')
  await page.locator('#loginUser').select_option('admin')
  await page.locator('#loginForm button').click()
  await page.locator('#loginOverlay').wait_for(state='hidden')
  await page.locator('[data-tab=templates]').click()
  await page.locator('#templateName').fill('V15 Regression Profile')
  await page.locator('[data-template-action=capture]').click()
  await page.wait_for_function("() => document.getElementById('templateSummary').textContent.includes('Draft')")
  await page.locator('[data-template-action=validate]').click()
  await page.wait_for_function("() => document.getElementById('templateSummary').textContent.includes('Validated')")
  await page.locator('[data-template-action=publish]').click()
  await page.wait_for_function("() => document.getElementById('templateSummary').textContent.includes('Published')")
  await page.locator('#upgradeStation').select_option('B')
  await page.locator('[data-upgrade=preflight]').click()
  await page.wait_for_function("() => document.getElementById('upgradePreflight').textContent.includes('预检通过')",timeout=5000)
  assert await page.locator('[data-upgrade=apply]').is_enabled()
  await page.locator('[data-upgrade=apply]').click()
  await page.wait_for_function("() => document.getElementById('upgradeHistory').textContent.includes('Succeeded')",timeout=7000)
  assert 'Succeeded' in await page.locator('#upgradeEvidence').inner_text()
  await page.screenshot(path=str(ROOT/'DeviceControl_V1.5_Upgrades.png'),full_page=True)
  await page.locator('[data-upgrade=rollback]').click()
  await page.wait_for_function("() => document.getElementById('upgradeHistory').textContent.includes('RolledBack')",timeout=7000)
  assert await page.locator('[data-upgrade=rollback]').is_disabled()
  # Set target station to auto -> preflight must be rejected; return it to manual.
  await page.locator('#workspaceStation').select_option('B')
  await page.wait_for_function("() => document.getElementById('workspaceTag').textContent==='工作区 B'")
  await page.locator('[data-tab=dashboard]').click()
  await page.locator('[data-mode=Auto]').click()
  await page.wait_for_function("() => document.getElementById('modeValue').textContent==='自动'",timeout=5000)
  await page.locator('[data-tab=templates]').click()
  await page.locator('[data-upgrade=preflight]').click()
  await page.wait_for_function("() => document.getElementById('upgradePreflight').textContent.includes('预检拒绝')",timeout=5000)
  assert await page.locator('[data-upgrade=apply]').is_disabled()
  assert '手动模式' in await page.locator('#upgradePreflight').inner_text()
  assert not errors,errors
  await browser.close()
  print('PASS V1.5 Chromium: publish+preflight+B deployment+history+rollback+auto-mode deny')
if __name__=='__main__':asyncio.run(run())
