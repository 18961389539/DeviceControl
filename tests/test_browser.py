"""V0.4 browser test: offline simulation only; no backend or external hardware."""
import asyncio
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
HTML=(ROOT/'tests'/'fixtures'/'DeviceControl_V0.4_Preview.html').read_text(encoding='utf-8')

async def login(page, name):
    await page.locator('#loginUser').select_option(name)
    await page.locator('#loginPassword').fill('demo123')
    await page.locator('#loginForm button[type="submit"]').click()
    await page.locator('#loginOverlay').wait_for(state='hidden')

async def test():
 async with async_playwright() as pw:
  browser=await pw.chromium.launch(executable_path='/usr/bin/chromium', headless=True, args=['--no-sandbox'])
  page=await browser.new_page(viewport={'width':1440,'height':960},accept_downloads=True)
  errors=[]
  page.on('pageerror',lambda e:errors.append(str(e)))
  await page.set_content(HTML,wait_until='load')
  # Operator may run but not access engineering controls.
  await login(page,'operator')
  await page.locator('nav [data-tab="manual"]').click()
  assert await page.locator('[data-action="servo-on"]').is_disabled()
  assert await page.locator('[data-mode="Manual"]').is_disabled()
  await page.locator('[data-tab="governance"]').click()
  assert await page.locator('[data-governance="draft"]').is_disabled()
  await page.locator('#logoutButton').click()
  await login(page,'engineer')
  await page.locator('nav [data-tab="manual"]').click()
  assert await page.locator('#axisSelect option').count()==2
  for axis in ('X','Y'):
   await page.locator('#axisSelect').select_option(axis)
   await page.locator('[data-action="servo-on"]').click()
   await page.wait_for_function('document.querySelector("#axisBadge").textContent==="已使能"')
   await page.locator('[data-action="axis-home"]').click()
   await page.wait_for_function('document.querySelector("#homedReadout").textContent==="已回零"')
  await page.locator('[data-tab="governance"]').click()
  assert await page.locator('.recipe-item').count()==1
  await page.locator('#recipeOffset').fill('2.5')
  await page.locator('[data-governance="draft"]').click()
  await page.wait_for_function('document.querySelectorAll(".recipe-item").length===2')
  await page.locator('[data-governance="validate"]').click()
  await page.wait_for_function('document.querySelector(".recipe-item.selected").textContent.includes("已验证")')
  assert await page.locator('[data-governance="publish"]').is_disabled()
  await page.screenshot(path=str(ROOT/'preview-governance.png'),full_page=True)
  await page.locator('#logoutButton').click()
  await login(page,'admin')
  await page.locator('[data-tab="governance"]').click()
  # Re-select previous engineer's validated draft.
  await page.locator('.recipe-item').filter(has_text='已验证').click()
  assert await page.locator('[data-governance="publish"]').is_enabled()
  await page.locator('[data-governance="publish"]').click()
  await page.wait_for_function('document.querySelector("#activeRecipeInfo").textContent.includes("2.50 mm")')
  assert await page.locator('#auditRows tr').count()>=2
  await page.locator('.recipe-item').filter(has_text='历史版本').first.click()
  assert await page.locator('[data-governance="rollback"]').is_enabled()
  page.once('dialog',lambda dialog:dialog.accept())
  await page.locator('[data-governance="rollback"]').click()
  await page.wait_for_function('document.querySelector("#activeRecipeInfo").textContent.includes("0.00 mm")')
  await page.locator('[data-tab="recovery"]').click()
  assert await page.locator('.check-row').count()==7
  await page.screenshot(path=str(ROOT/'preview-recovery.png'),full_page=True)
  await page.locator('[data-tab="dashboard"]').click()
  await page.locator('[data-mode="Auto"]').click()
  await page.locator('[data-action="cycle-start"]').click()
  await page.wait_for_function('document.querySelector("#cyclesValue").textContent==="1"',timeout=30000)
  await page.locator('[data-tab="analytics"]').click()
  assert await page.locator('.history-row').count()==1
  assert await page.locator('.timing-row').count()==11
  assert '配方' in await page.locator('#selectedRunLabel').inner_text()
  await page.screenshot(path=str(ROOT/'preview-analytics.png'),full_page=True)
  # Failure scenario and recovery checklist.
  await page.locator('[data-tab="io"]').click()
  await page.locator('[data-signal-name="ClampStuck"]').click()
  await page.locator('[data-tab="dashboard"]').click()
  await page.locator('[data-action="cycle-start"]').click()
  await page.wait_for_function('document.querySelector("#stateValue").textContent==="故障"',timeout=30000)
  await page.locator('[data-tab="recovery"]').click()
  assert await page.locator('.check-row.fail').count()>=1
  assert 'ACT-CYL-TIMEOUT' in await page.locator('#guideAlarm').inner_text()
  await page.screenshot(path=str(ROOT/'preview-recovery-fault.png'),full_page=True)
  assert not errors, errors
  await browser.close()
 print('PASS V0.4 browser UI: role switching, forbidden controls, draft, validation, publish, rollback, audit, cycle, recipe binding, fault checklist')

if __name__=='__main__':asyncio.run(test())
