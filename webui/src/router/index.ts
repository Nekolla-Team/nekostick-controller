import {
  createRouter,
  createWebHashHistory,
  type RouteRecordRaw,
} from 'vue-router'
import { connection } from '../stores/connection'

const routes: RouteRecordRaw[] = [
  {
    path: '/connect',
    name: 'connect',
    component: () => import('../views/ConnectView.vue'),
  },
  {
    path: '/',
    name: 'dashboard',
    component: () => import('../views/DashboardView.vue'),
  },
  {
    path: '/routes',
    name: 'routes',
    component: () => import('../views/RoutesView.vue'),
  },
  {
    path: '/services',
    name: 'services',
    component: () => import('../views/ServicesView.vue'),
  },
  {
    path: '/services/:id/runtime',
    name: 'service-runtime',
    component: () => import('../views/ServiceRuntimeView.vue'),
  },
  {
    path: '/extensions',
    name: 'extensions',
    component: () => import('../views/ExtensionsView.vue'),
  },
  {
    path: '/global-settings',
    name: 'global-settings',
    component: () => import('../views/GlobalSettingsView.vue'),
  },
  {
    path: '/:pathMatch(.*)*',
    redirect: '/',
  },
]

const router = createRouter({
  history: createWebHashHistory(),
  routes,
})

router.beforeEach((to) => {
  if (!connection.apiKey && to.path !== '/connect') return '/connect'
  return true
})

export default router
