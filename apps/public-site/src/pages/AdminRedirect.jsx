import { useEffect } from 'react';
import { useLocation } from 'react-router-dom';

export default function AdminRedirect() {
  const location = useLocation();
  useEffect(() => {
    window.location.replace(location.pathname + location.search);
  }, []);
  return null;
}
