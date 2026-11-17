// Explicitly rotates only the three local 5203 Demo accounts through their real UI.
// Password values remain in the protected local files and are never printed.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
const require=createRequire(import.meta.url);
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const read=async file=>JSON.parse((await readFile(file,'utf8')).replace(/^\uFEFF/,''));
const old=await read(process.env.QA_OLD_FILE), next=await read(process.env.QA_NEW_FILE);
const base='http://127.0.0.1:5203';
const browser=await chromium.launch({executablePath:process.env.BROWSER_EXECUTABLE || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',headless:true});
try {
  for(const role of ['Customer','Admin','SuperAdmin']) {
    const ctx=await browser.newContext(); const page=await ctx.newPage();
    try {
      const login=async password=>{
        await page.goto(base+'/Account/Login');
        await page.locator('[name=email]').fill(next[role].Email);
        await page.locator('[name=password]').fill(password);
        await Promise.all([page.waitForResponse(r=>r.url().includes('/Account/Login')&&r.request().method()==='POST'),page.locator('#auth-form button.button').click()]);
        await page.waitForLoadState('load');
      };
      await login(old[role]);
      assert.ok(!new URL(page.url()).pathname.includes('/Account/Login'),`${role}: current login failed, no rotation attempted`);
      console.log(`PASS ${role}: current file credentials work on the login form`);
      await page.goto(base+'/Account/Settings');
      const change=page.locator('form[action="/Account/ChangePassword"]');
      await change.locator('[name=currentPassword]').fill(old[role]);
      await change.locator('[name=newPassword]').fill(next[role].Password);
      await change.locator('[name=confirmPassword]').fill(next[role].Password);
      await Promise.all([page.waitForResponse(r=>r.url().includes('/Account/ChangePassword')&&r.request().method()==='POST'),change.locator('button[type=submit], button:not([type])').last().click()]);
      await page.waitForLoadState('load');
      assert.ok((await page.locator('body').innerText()).includes('Password changed. Sign in with your new password.'),`${role}: password change was not confirmed; do not retry blindly`);
      console.log(`PASS ${role}: password rotated and session signed out`);
      await login(old[role]);
      assert.ok((await page.locator('body').innerText()).includes('Email or password is incorrect.'),`${role}: old credential was not rejected`);
      console.log(`PASS ${role}: old credential rejected`);
      await login(next[role].Password);
      assert.ok(!new URL(page.url()).pathname.includes('/Account/Login'),`${role}: new credential failed`);
      await page.goto(base+'/Account/Settings');
      assert.ok(new URL(page.url()).pathname==='/Account/Settings',`${role}: new session not accepted`);
      console.log(`PASS ${role}: new password signs in and session works`);
      await Promise.all([page.waitForURL(u=>!u.pathname.includes('/Account/Settings')),page.locator('form[action="/Account/Logout"] button').last().click()]);
      const check=await ctx.request.get(base+'/Account/Settings',{maxRedirects:0});
      assert.ok(check.status()===302 && /Login/.test(check.headers().location || ''),`${role}: logout did not terminate session`);
      console.log(`PASS ${role}: sign-out confirmed`);
    } finally {await ctx.close();}
  }
} finally {await browser.close();}
