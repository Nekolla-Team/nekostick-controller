import { app } from './app'
import { common } from './common'
import { errors } from './errors'
import { connect } from './connect'
import { dashboard } from './dashboard'
import { extensions } from './extensions'
import { globalSettings } from './globalSettings'
import { routes } from './routes'
import { services } from './services'
import { serviceRuntime } from './serviceRuntime'

export const zhCN = {
  app,
  common,
  errors,
  connect,
  dashboard,
  extensions,
  globalSettings,
  routes,
  services,
  serviceRuntime,
} as const
