use std::io::{BufRead, BufReader, BufWriter, Write};

type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Sync + Send>>;

fn handle_connection(node_name: String, conn: std::net::TcpStream) -> Result<()> {
  let mut reader = BufReader::new(conn.try_clone()?);
  let mut writer = BufWriter::new(conn.try_clone()?);

  let mut line = String::new();
  reader.read_line(&mut line)?;
  let line = line.trim();
  let parts = line.split(' ').collect::<Vec<_>>();
  let (method, path, _http_version) = (parts[0], parts[1], parts[2]);

  let mut headers = std::collections::HashMap::new();
  loop {
    let mut line = String::new();
    reader.read_line(&mut line)?;
    let line = line.trim();
    if line.is_empty() {
      break;
    }

    let parts = line.split(':').collect::<Vec<_>>();
    headers.insert(parts[0].trim().to_ascii_lowercase(), parts[1].trim().to_owned());
  }

  let mut wr = |s: &str| -> Result<()> {
    writer.write_all(s.as_bytes())?;
    Ok(())
  };

  match (method, path) {
    ("GET", "/") => {
      wr("HTTP/1.1 200 OK\r\n")?;
      wr("Connection: close\r\n")?;
      wr("Content-Type: text/html\r\n")?;
      wr("\r\n")?;

      wr("<html><head>")?;
      wr("<meta charset=\"utf-8\">")?;
      wr("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")?;
      wr("<title>Smolmesh2 demo dashboard</title>")?;
      wr("</head><body>")?;

      wr("<style>")?;
      wr("body { font-family: sans-serif; }")?;
      wr("table { border-collapse: collapse; }")?;
      wr("table, th, td { border: 1px solid black; }")?;
      wr("td, th { padding: 5px; }")?;
      wr("</style>")?;

      // Table
      wr("<table>")?;
      wr("<tr><th>Node</th><th>IPv6</th><th>Last seen</th><th>Traceroute</th></tr>")?;
      for (node, last_seen) in crate::recently_seen_nodes::get().recent_list() {
        wr("<tr>")?;
        wr(&format!("<td>{}</td>", node))?;
        wr(&format!(
          "<td>{}</td>",
          crate::ipv6_addr::Addr::from_node_name(&node).to_ipv6()
        ))?;
        wr(&format!(
          "<td>{} seconds ago</td>",
          ((crate::millis() - last_seen) as f64 / 1000.0) as i64
        ))?;

        if let Some(traceroute) = crate::recently_seen_nodes::get().get_traceroute(&node) {
          wr(&format!("<td>{}</td>", traceroute))?;
        } else {
          wr("<td></td>")?;
        }
        wr("</tr>")?;
      }
      wr("</table>")?;

      wr("<br/><a href=\"/traceroute_all\">Traceroute all</a>")?;

      wr("</body></html>")?;
    }
    ("GET", "/traceroute_all") => {
      wr("HTTP/1.1 302 Found\r\n")?;
      wr("Connection: close\r\n")?;
      wr("Location: /\r\n")?;
      wr("\r\n")?;

      crate::recently_seen_nodes::get().clear_traceroutes();

      for (node, _) in crate::recently_seen_nodes::get().recent_list() {
        let mut resp = Vec::new();
        resp.write_all(&crate::millis().to_le_bytes())?;
        resp.push(5);

        resp.push(node.len() as u8);
        resp.extend_from_slice(node.as_bytes());
        resp.push(node_name.len() as u8);
        resp.extend_from_slice(node_name.as_bytes());

        resp.extend_from_slice(format!("{} ", node_name).as_bytes());

        crate::all_senders::get().send_to_fastest(crate::ipv6_addr::Addr::from_node_name(&node), resp);
      }

      std::thread::sleep(std::time::Duration::from_secs(2));
    }
    ("GET", "/etc/hosts") => {
      wr("HTTP/1.1 200 OK\r\n")?;
      wr("Connection: close\r\n")?;
      wr("Content-Type: text/plain\r\n")?;
      wr("\r\n")?;

      wr("# smolmesh2 known hosts\n\n")?;

      for (node, _) in crate::recently_seen_nodes::get().recent_list() {
        let addr = crate::ipv6_addr::Addr::from_node_name(&node).to_ipv6();
        wr(&format!("{} {}\n", addr, node))?;
      }

      wr("\n# end of smolmesh2 known hosts\n")?;
    }
    (&_, &_) => {
      writer.write_all(b"HTTP/1.1 404 Not Found\r\n").unwrap();
      writer.write_all(b"Connection: close\r\n").unwrap();
      writer.write_all(b"Content-Type: text/html\r\n").unwrap();
      writer.write_all(b"\r\n").unwrap();
      writer
        .write_all(b"<html><body><h1>Not Found</h1></body></html>\r\n")
        .unwrap();
    }
  }

  Ok(())
}

fn admin_server_impl(node_name: String, port: u16) {
  let listener = std::net::TcpListener::bind(format!(":::{}", port)).unwrap();

  for stream in listener.incoming() {
    let stream = stream.unwrap();
    let node_name = node_name.clone();
    std::thread::spawn(move || handle_connection(node_name, stream));
  }
}

pub fn start_admin_server(node_name: String, port: u16) {
  std::thread::spawn(move || admin_server_impl(node_name, port));
}
