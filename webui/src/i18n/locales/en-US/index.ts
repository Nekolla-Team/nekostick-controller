import type { zhCN } from '../zh-CN'
import { app } from './app'
import { common } from './common'
import { errors } from './errors'
import { connect } from './connect'
import { controllerConfig } from './controllerConfig'
import { dashboard } from './dashboard'
import { extensions } from './extensions'
import { globalSettings } from './globalSettings'
import { routes } from './routes'
import { services } from './services'
import { serviceRuntime } from './serviceRuntime'

type Widen<T> = T extends string ? string : { [K in keyof T]: Widen<T[K]> }

export const enUS: Widen<typeof zhCN> = {
  app,
  common,
  errors,
  connect,
  controllerConfig,
  dashboard,
  extensions,
  globalSettings,
  routes,
  services,
  serviceRuntime,
}
