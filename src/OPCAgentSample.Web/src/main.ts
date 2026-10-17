import { createPinia } from 'pinia';
import { createApp } from 'vue';
import App from './App.vue';
import { reloadOnServiceWorkerUpdate } from './platform/updates';
import './styles/index.css';

reloadOnServiceWorkerUpdate(
  'serviceWorker' in navigator ? navigator.serviceWorker : undefined,
  () => window.location.reload(),
);

createApp(App).use(createPinia()).mount('#app');
