/* Semantic Router dashboard: data-action handlers that page modules register themselves. Imports nothing. */

/*
 * The click dispatcher in app.js looks a data-action up in actions.js first and then here (it imports this object as
 * feedback.js's reportActions). A page module adds its own handlers with Object.assign(pageActions, { … }), under
 * names actions.js does not use, so a page can grow a button without editing the shared action table.
 */
export const pageActions = {};
