#!/usr/bin/env python3
"""Focused offline tests of the existing single-rule synchronizer."""
import pathlib, subprocess, tempfile, unittest
SCRIPT=pathlib.Path(__file__).with_name('sync-local-azure-sql-firewall.sh').resolve()
class FirewallTests(unittest.TestCase):
 def run_case(self, mode):
  with tempfile.TemporaryDirectory() as temp:
   body=f'''source '{SCRIPT}'
resolve_azure_cli() {{ :; }}
current_public_ipv4() {{ echo 45.86.210.87; }}
assert_azure_identity() {{ :; }}
azure() {{
 echo "$@" >> '{temp}/calls'
 case "$*" in
 *create*) touch '{temp}/written'; [[ '{mode}' != ambiguous ]];;
 *show*)
 [[ '{mode}' != unreadable ]] || return 1
 if [[ -e '{temp}/written' && '{mode}' != mismatch || '{mode}' == unchanged ]]; then
 printf '45.86.210.87\\t45.86.210.87\\n'
 else printf '45.86.210.87\\t45.86.210.99\\n'; fi;;
 esac
}}
main
'''
   r=subprocess.run(['bash','-c',body],capture_output=True,text=True)
   calls=pathlib.Path(temp,'calls').read_text()
   return r,calls
 def test_unchanged_no_write(self):
  r,c=self.run_case('unchanged');self.assertEqual(r.returncode,0);self.assertNotIn('create',c)
 def test_wide_endpoint_repaired_and_read_back(self):
  r,c=self.run_case('wide');self.assertEqual(r.returncode,0);self.assertEqual(c.count('create'),1);self.assertEqual(c.count('firewall-rule show'),2)
 def test_read_failure_never_writes(self):
  r,c=self.run_case('unreadable');self.assertNotEqual(r.returncode,0);self.assertNotIn('create',c)
 def test_ambiguous_write_reconciles_without_retry(self):
  r,c=self.run_case('ambiguous');self.assertEqual(r.returncode,0);self.assertEqual(c.count('create'),1)
 def test_readback_mismatch_fails(self):
  r,c=self.run_case('mismatch');self.assertNotEqual(r.returncode,0);self.assertEqual(c.count('create'),1)
 def test_hung_command_bounded(self):
  r=subprocess.run(['bash','-c',f"source '{SCRIPT}'; run_bounded 0.1 python3 -c 'import time; time.sleep(30)'"],capture_output=True,text=True,timeout=3)
  self.assertEqual(r.returncode,124)
 def test_hung_descendant_is_killed(self):
  with tempfile.TemporaryDirectory() as temp:
   marker=pathlib.Path(temp,'survived')
   command=f"source '{SCRIPT}'; run_bounded 0.1 bash -c 'bash -c \"sleep 0.5; touch {marker}\" & wait' "
   r=subprocess.run(['bash','-c',command],capture_output=True,text=True,timeout=3)
   self.assertEqual(r.returncode,124)
   import time
   time.sleep(0.6)
   self.assertFalse(marker.exists())
 def test_wrong_subscription_denied(self):
  r=subprocess.run(['bash','-c',f"source '{SCRIPT}'; azure() {{ echo wrong-subscription; }}; assert_azure_identity"],capture_output=True,text=True)
  self.assertNotEqual(r.returncode,0)
 def test_wrong_server_denied(self):
  r=subprocess.run(['bash','-c',f"source '{SCRIPT}'; azure() {{ if [[ $1 == account ]]; then echo $SUBSCRIPTION_ID; else echo wrong-server; fi; }}; assert_azure_identity"],capture_output=True,text=True)
  self.assertNotEqual(r.returncode,0)
 def test_private_and_special_addresses_rejected(self):
  for ip in ['0.0.0.0','127.0.0.1','10.1.1.1','192.168.1.1','999.1.1.1']:
   r=subprocess.run(['bash','-c',f"source '{SCRIPT}'; is_public_ipv4 '{ip}'"]);self.assertNotEqual(r.returncode,0)
if __name__=='__main__': unittest.main()
