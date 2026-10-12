// Send the TCP traffic of selected processes through ProcTamer's local relay
// (SOCKS5 on 127.0.0.1:17877) so ProcTamer can limit their upload rate while
// TUN mode is on. The SOCKS5 username carries the process name.
// Each process gets a fallback group [relay, DIRECT]: when ProcTamer is not
// running the health check fails and the traffic goes DIRECT.
// Meant for Clash Verge's global script (Script.js); it preserves the
// subscription's existing proxies, groups and rules.
function main(config, profileName) {
  const RELAY_SERVER = "127.0.0.1";
  const RELAY_PORT = 17877;
  const CHECK_URL = "http://www.msftconnecttest.com/connecttest.txt";
  const PROCESSES = [
    "完美世界竞技平台.exe",
    "pwautil64.exe",
    "WmgpOptimizer_x64.exe",
    "5EClient.exe",
    "Bucky64.exe"
  ];

  const relayProxies = [];
  const relayGroups = [];
  // ProcTamer's own outbound connections must never be routed back into the relay.
  const relayRules = ["PROCESS-NAME,ProcTamer.exe,DIRECT"];

  PROCESSES.forEach((exe) => {
    const proxyName = "ProcTamer-Relay " + exe;
    const groupName = "ProcTamer " + exe;
    relayProxies.push({
      name: proxyName,
      type: "socks5",
      server: RELAY_SERVER,
      port: RELAY_PORT,
      username: exe,
      password: "proctamer",
      udp: false
    });
    relayGroups.push({
      name: groupName,
      type: "fallback",
      proxies: [proxyName, "DIRECT"],
      url: CHECK_URL,
      interval: 30,
      timeout: 3000,
      lazy: false
    });
    relayRules.push("AND,((PROCESS-NAME," + exe + "),(NETWORK,TCP))," + groupName);
  });

  // Drop anything left over from an earlier run so the script can be applied twice.
  const proxyNames = relayProxies.map((item) => item.name);
  const groupNames = relayGroups.map((item) => item.name);
  const oldProxies = Array.isArray(config.proxies) ? config.proxies : [];
  const oldGroups = Array.isArray(config["proxy-groups"]) ? config["proxy-groups"] : [];
  const oldRules = Array.isArray(config.rules) ? config.rules : [];

  config.proxies = oldProxies
    .filter((item) => !item || !proxyNames.includes(item.name))
    .concat(relayProxies);
  config["proxy-groups"] = oldGroups
    .filter((item) => !item || !groupNames.includes(item.name))
    .concat(relayGroups);
  config.rules = relayRules.concat(oldRules.filter((item) => !relayRules.includes(item)));

  return config;
}
