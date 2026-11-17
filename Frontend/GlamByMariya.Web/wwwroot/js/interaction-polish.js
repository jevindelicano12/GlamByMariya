// Native submission remains responsible for the action value and antiforgery.
document.querySelectorAll('.staff-layout form[action$="/Admin/Action"]').forEach(form => {
  let pending = false;
  let previouslyDisabled = [];
  let submittedValue = null;
  const status = document.createElement('p');
  status.className = 'staff-action-status';
  status.setAttribute('role', 'status');
  status.hidden = true;
  form.append(status);
  const reset = () => {
    pending = false;
    previouslyDisabled.forEach(({ button, disabled }) => { button.disabled = disabled; });
    previouslyDisabled = [];
    submittedValue?.remove();
    submittedValue = null;
    form.removeAttribute('aria-busy');
    status.hidden = true;
    status.textContent = '';
  };
  form.addEventListener('submit', event => {
    if (event.defaultPrevented) return;
    if (pending) { event.preventDefault(); return; }
    pending = true;
    if (event.submitter?.name) {
      submittedValue = document.createElement('input');
      submittedValue.type = 'hidden';
      submittedValue.name = event.submitter.name;
      submittedValue.value = event.submitter.value;
      form.append(submittedValue);
    }
    form.setAttribute('aria-busy', 'true');
    status.textContent = 'Sending request…';
    status.hidden = false;
    previouslyDisabled = [...form.querySelectorAll('button[type="submit"], button:not([type])')]
      .map(button => ({ button, disabled: button.disabled }));
    previouslyDisabled.forEach(({ button }) => { button.disabled = true; });
    window.setTimeout(() => {
      if (event.defaultPrevented) { reset(); return; }
    }, 0);
  });
  window.addEventListener('pageshow', reset);
});
