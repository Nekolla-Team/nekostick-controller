import { createApp, defineComponent, h, nextTick } from 'vue';
import { NMessageProvider } from 'naive-ui';
import { describe, expect, it } from 'vitest';
import { ApiClientError } from '../src/api/client';
import ApiErrorDetailsDialog from '../src/components/ApiErrorDetailsDialog.vue';

describe('ApiErrorDetailsDialog', () => {
  it('renders the reason and message when optional details are null', async () => {
    const message = 'The extension settings document has not been created yet.';
    const error = new ApiClientError(message, {
      status: 404,
      code: 'no_settings',
      kind: 'no_settings',
      details: {
        reason: 'no_settings',
        parameter: null,
        expected: null,
        actual: null,
        traceId: null,
      },
    });
    const host = document.createElement('div');
    document.body.append(host);
    const app = createApp(defineComponent({
      render: () => h(NMessageProvider, null, {
        default: () => h(ApiErrorDetailsDialog, { error, show: true }),
      }),
    }));

    try {
      app.mount(host);
      await nextTick();
      const dialog = document.querySelector('.api-error-details-dialog');
      expect(dialog?.textContent).toContain('no_settings');
      expect(dialog?.textContent).toContain(message);
    } finally {
      app.unmount();
      host.remove();
    }
  });
});
